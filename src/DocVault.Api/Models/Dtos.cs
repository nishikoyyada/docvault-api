using System.ComponentModel.DataAnnotations;

namespace DocVault.Api.Models;

public sealed class UploadDocumentRequest
{
    [Required] public IFormFile File { get; set; } = default!;
    [Required, MaxLength(200)] public string Title { get; set; } = string.Empty;
    [Required, MaxLength(50)] public string Category { get; set; } = string.Empty;
    /// <summary>When false (default), uploading a file that already exists returns 409.</summary>
    public bool AllowDuplicate { get; set; }
}

public sealed class UploadVersionRequest
{
    [Required] public IFormFile File { get; set; } = default!;
}

public sealed record DocumentListItem(
    long SeqId, Guid DocumentId, string Title, string Category, int CurrentVersion,
    DateTime UpdatedOn, string FileName, long SizeBytes, string ContentType);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, long? NextCursor);

public sealed record DocumentVersionInfo(
    int VersionNo, string FileName, string ContentType, long SizeBytes,
    string Sha256, string UploadedBy, DateTime UploadedOn);

public sealed record DocumentDetails(
    Guid DocumentId, string Title, string Category, int CurrentVersion,
    string CreatedBy, DateTime CreatedOn, DateTime UpdatedOn,
    IReadOnlyList<DocumentVersionInfo> Versions);

public sealed record DownloadInfo(
    int VersionNo, string BlobName, string FileName, string ContentType, long SizeBytes);

public sealed record AuditEntry(string Action, int? VersionNo, string UserName, DateTime ActionOn);

public sealed record UploadResult(Guid DocumentId, int VersionNo, string Sha256);

public sealed record DownloadLink(string Url, DateTimeOffset ExpiresOn);

public sealed record NewFile(
    string BlobName, string FileName, string ContentType, long SizeBytes, string Sha256);
