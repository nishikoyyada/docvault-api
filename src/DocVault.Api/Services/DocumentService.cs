using System.Security.Cryptography;
using DocVault.Api.Data;
using DocVault.Api.Middleware;
using DocVault.Api.Models;
using DocVault.Api.Options;
using Microsoft.Extensions.Options;

namespace DocVault.Api.Services;

public interface IDocumentService
{
    Task<UploadResult> UploadAsync(UploadDocumentRequest request, string user, CancellationToken ct);
    Task<UploadResult> AddVersionAsync(Guid documentId, IFormFile file, string user, CancellationToken ct);
    Task<PagedResult<DocumentListItem>> ListAsync(string? category, string? search, long afterSeqId, int pageSize, CancellationToken ct);
    Task<DocumentDetails> GetAsync(Guid documentId, CancellationToken ct);
    Task<(Stream Content, DownloadInfo Info)> DownloadAsync(Guid documentId, int? version, string user, CancellationToken ct);
    Task<DownloadLink> GetDownloadLinkAsync(Guid documentId, int? version, string user, CancellationToken ct);
    Task DeleteAsync(Guid documentId, string user, CancellationToken ct);
    Task<IReadOnlyList<AuditEntry>> GetAuditAsync(Guid documentId, CancellationToken ct);
}

public sealed class DocumentService(
    IDocumentRepository repo,
    IBlobStorageService blobs,
    IOptions<StorageOptions> options,
    ILogger<DocumentService> logger) : IDocumentService
{
    private readonly StorageOptions _opt = options.Value;

    public async Task<UploadResult> UploadAsync(UploadDocumentRequest request, string user, CancellationToken ct)
    {
        var file = await PrepareFileAsync(request.File, ct);

        if (!request.AllowDuplicate)
        {
            var existing = await repo.FindByHashAsync(file.Sha256, ct);
            if (existing is not null)
                throw new ConflictException(
                    $"This file already exists as document {existing.Value.DocumentId} (version {existing.Value.VersionNo}).");
        }

        var documentId = Guid.NewGuid();
        var newFile = await StoreBlobAsync(documentId, request.File, file, ct);

        await SaveOrCleanUpAsync(newFile.BlobName,
            () => repo.CreateAsync(documentId, request.Title.Trim(), request.Category.Trim(), newFile, user, ct));

        logger.LogInformation("Document {DocumentId} uploaded by {User} ({Size} bytes)", documentId, user, newFile.SizeBytes);
        return new UploadResult(documentId, 1, newFile.Sha256);
    }

    public async Task<UploadResult> AddVersionAsync(Guid documentId, IFormFile formFile, string user, CancellationToken ct)
    {
        var file = await PrepareFileAsync(formFile, ct);
        var newFile = await StoreBlobAsync(documentId, formFile, file, ct);

        var version = 0;
        await SaveOrCleanUpAsync(newFile.BlobName, async () =>
            version = await repo.AddVersionAsync(documentId, newFile, user, ct));

        logger.LogInformation("Document {DocumentId} version {Version} uploaded by {User}", documentId, version, user);
        return new UploadResult(documentId, version, newFile.Sha256);
    }

    public async Task<PagedResult<DocumentListItem>> ListAsync(string? category, string? search, long afterSeqId, int pageSize, CancellationToken ct)
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        var items = await repo.ListAsync(Blank(category), Blank(search), Math.Max(afterSeqId, 0), pageSize, ct);
        // A full page means there may be more rows: return the last SeqId as the next cursor
        long? next = items.Count == pageSize ? items[^1].SeqId : null;
        return new PagedResult<DocumentListItem>(items, next);
    }

    public async Task<DocumentDetails> GetAsync(Guid documentId, CancellationToken ct) =>
        await repo.GetAsync(documentId, ct) ?? throw new NotFoundException($"Document {documentId} was not found.");

    public async Task<(Stream Content, DownloadInfo Info)> DownloadAsync(Guid documentId, int? version, string user, CancellationToken ct)
    {
        var info = await GetDownloadInfoAsync(documentId, version, user, ct);
        var stream = await blobs.OpenReadAsync(info.BlobName, ct);
        return (stream, info);
    }

    public async Task<DownloadLink> GetDownloadLinkAsync(Guid documentId, int? version, string user, CancellationToken ct)
    {
        var info = await GetDownloadInfoAsync(documentId, version, user, ct);
        var expiresOn = DateTimeOffset.UtcNow.AddMinutes(_opt.SasExpiryMinutes);
        var uri = blobs.GetReadSasUri(info.BlobName, info.FileName, expiresOn);
        return new DownloadLink(uri.ToString(), expiresOn);
    }

    public async Task DeleteAsync(Guid documentId, string user, CancellationToken ct)
    {
        // Soft delete: the row is hidden and audited; blobs are kept for recovery / retention rules.
        if (!await repo.SoftDeleteAsync(documentId, user, ct))
            throw new NotFoundException($"Document {documentId} was not found.");
    }

    public async Task<IReadOnlyList<AuditEntry>> GetAuditAsync(Guid documentId, CancellationToken ct)
    {
        _ = await GetAsync(documentId, ct); // 404 if the document does not exist
        return await repo.GetAuditAsync(documentId, ct);
    }

    // ---------- helpers ----------

    private async Task<DownloadInfo> GetDownloadInfoAsync(Guid documentId, int? version, string user, CancellationToken ct)
    {
        if (version is <= 0) throw new BadRequestException("Version must be 1 or more.");
        return await repo.GetVersionForDownloadAsync(documentId, version, user, ct)
               ?? throw new NotFoundException(version is null
                   ? $"Document {documentId} was not found."
                   : $"Version {version} of document {documentId} was not found.");
    }

    private sealed record PreparedFile(string FileName, string Extension, string ContentType, long SizeBytes, string Sha256);

    private async Task<PreparedFile> PrepareFileAsync(IFormFile file, CancellationToken ct)
    {
        if (file.Length == 0) throw new BadRequestException("The file is empty.");
        if (file.Length > _opt.MaxFileSizeBytes)
            throw new BadRequestException($"The file is larger than the limit of {_opt.MaxFileSizeBytes / (1024 * 1024)} MB.");

        var fileName = Path.GetFileName(file.FileName);   // strips any folder path sent by the client
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (!_opt.AllowedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            throw new BadRequestException($"File type '{ext}' is not allowed.");

        await using var stream = file.OpenReadStream();
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();

        var contentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType;
        return new PreparedFile(fileName, ext, contentType, file.Length, hash);
    }

    private async Task<NewFile> StoreBlobAsync(Guid documentId, IFormFile formFile, PreparedFile file, CancellationToken ct)
    {
        // Unique blob name per upload: files are never overwritten, every version is kept.
        var blobName = $"{documentId:N}/{Guid.NewGuid():N}{file.Extension}";
        await using var stream = formFile.OpenReadStream();
        await blobs.UploadAsync(blobName, stream, file.ContentType, ct);
        return new NewFile(blobName, file.FileName, file.ContentType, file.SizeBytes, file.Sha256);
    }

    /// <summary>
    /// Blob upload and SQL insert can't share one transaction.
    /// If the SQL step fails, delete the blob we just uploaded so no orphan files are left.
    /// </summary>
    private async Task SaveOrCleanUpAsync(string blobName, Func<Task> saveToDb)
    {
        try
        {
            await saveToDb();
        }
        catch
        {
            try { await blobs.DeleteIfExistsAsync(blobName, CancellationToken.None); }
            catch (Exception cleanupEx) { logger.LogWarning(cleanupEx, "Could not remove orphan blob {BlobName}", blobName); }
            throw;
        }
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
