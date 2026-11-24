namespace DocVault.Api.Options;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public string ConnectionString { get; set; } = string.Empty;
    public string ContainerName { get; set; } = "documents";
    public long MaxFileSizeBytes { get; set; } = 25 * 1024 * 1024;
    public string[] AllowedExtensions { get; set; } = Array.Empty<string>();
    public int SasExpiryMinutes { get; set; } = 10;
}
