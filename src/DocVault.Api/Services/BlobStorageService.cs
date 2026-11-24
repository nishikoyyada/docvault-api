using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using DocVault.Api.Options;
using Microsoft.Extensions.Options;

namespace DocVault.Api.Services;

public interface IBlobStorageService
{
    Task EnsureContainerAsync(CancellationToken ct);
    Task UploadAsync(string blobName, Stream content, string contentType, CancellationToken ct);
    Task<Stream> OpenReadAsync(string blobName, CancellationToken ct);
    Uri GetReadSasUri(string blobName, string downloadFileName, DateTimeOffset expiresOn);
    Task DeleteIfExistsAsync(string blobName, CancellationToken ct);
}

public sealed class BlobStorageService : IBlobStorageService
{
    private readonly BlobContainerClient _container;

    public BlobStorageService(IOptions<StorageOptions> options)
    {
        var o = options.Value;
        _container = new BlobServiceClient(o.ConnectionString).GetBlobContainerClient(o.ContainerName);
    }

    // Container is private: files are only reachable through this API or a short-lived SAS link.
    public Task EnsureContainerAsync(CancellationToken ct) =>
        _container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: ct);

    public async Task UploadAsync(string blobName, Stream content, string contentType, CancellationToken ct)
    {
        var blob = _container.GetBlobClient(blobName);
        await blob.UploadAsync(content, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = contentType },
            // Large files are sent in 4 MB blocks, up to 4 blocks in parallel
            TransferOptions = new Azure.Storage.StorageTransferOptions
            {
                MaximumTransferSize = 4 * 1024 * 1024,
                MaximumConcurrency = 4
            }
        }, ct);
    }

    public Task<Stream> OpenReadAsync(string blobName, CancellationToken ct) =>
        _container.GetBlobClient(blobName).OpenReadAsync(cancellationToken: ct);

    public Uri GetReadSasUri(string blobName, string downloadFileName, DateTimeOffset expiresOn)
    {
        var blob = _container.GetBlobClient(blobName);
        if (!blob.CanGenerateSasUri)
            throw new InvalidOperationException("SAS links need a storage connection string with an account key.");

        var sas = new BlobSasBuilder(BlobSasPermissions.Read, expiresOn)
        {
            BlobContainerName = _container.Name,
            BlobName = blobName,
            Resource = "b",
            // Browser saves the file with its original name
            ContentDisposition = $"attachment; filename=\"{downloadFileName.Replace("\"", "")}\""
        };
        return blob.GenerateSasUri(sas);
    }

    public Task DeleteIfExistsAsync(string blobName, CancellationToken ct) =>
        _container.GetBlobClient(blobName).DeleteIfExistsAsync(cancellationToken: ct);
}
