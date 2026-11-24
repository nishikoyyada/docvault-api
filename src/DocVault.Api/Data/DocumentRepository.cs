using System.Data;
using DocVault.Api.Middleware;
using DocVault.Api.Models;
using Microsoft.Data.SqlClient;

namespace DocVault.Api.Data;

public interface IDocumentRepository
{
    Task CreateAsync(Guid documentId, string title, string category, NewFile file, string user, CancellationToken ct);
    Task<int> AddVersionAsync(Guid documentId, NewFile file, string user, CancellationToken ct);
    Task<IReadOnlyList<DocumentListItem>> ListAsync(string? category, string? search, long afterSeqId, int pageSize, CancellationToken ct);
    Task<DocumentDetails?> GetAsync(Guid documentId, CancellationToken ct);
    Task<DownloadInfo?> GetVersionForDownloadAsync(Guid documentId, int? versionNo, string user, CancellationToken ct);
    Task<bool> SoftDeleteAsync(Guid documentId, string user, CancellationToken ct);
    Task<(Guid DocumentId, int VersionNo)?> FindByHashAsync(string sha256, CancellationToken ct);
    Task<IReadOnlyList<AuditEntry>> GetAuditAsync(Guid documentId, CancellationToken ct);
}

/// <summary>
/// Data access with ADO.NET and stored procedures.
/// Every parameter has an explicit SqlDbType and size, so SQL Server never has to
/// convert types (no NVARCHAR vs VARCHAR mismatches) and plans are reused.
/// </summary>
public sealed class DocumentRepository(IConfiguration config) : IDocumentRepository
{
    private readonly string _connectionString =
        config.GetConnectionString("DocVaultDb") ?? throw new InvalidOperationException("Missing connection string 'DocVaultDb'.");

    public async Task CreateAsync(Guid documentId, string title, string category, NewFile file, string user, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = Proc(conn, "dbo.usp_Document_Create");
        cmd.Parameters.Add("@DocumentId", SqlDbType.UniqueIdentifier).Value = documentId;
        cmd.Parameters.Add("@Title", SqlDbType.NVarChar, 200).Value = title;
        cmd.Parameters.Add("@Category", SqlDbType.VarChar, 50).Value = category;
        AddFileParams(cmd, file, user);
        await conn.OpenAsync(ct);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> AddVersionAsync(Guid documentId, NewFile file, string user, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = Proc(conn, "dbo.usp_Document_AddVersion");
        cmd.Parameters.Add("@DocumentId", SqlDbType.UniqueIdentifier).Value = documentId;
        AddFileParams(cmd, file, user);
        var output = cmd.Parameters.Add("@NewVersion", SqlDbType.Int);
        output.Direction = ParameterDirection.Output;

        await conn.OpenAsync(ct);
        try
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (SqlException ex) when (ex.Number == 50404)
        {
            throw new NotFoundException($"Document {documentId} was not found.");
        }
        return (int)output.Value;
    }

    public async Task<IReadOnlyList<DocumentListItem>> ListAsync(string? category, string? search, long afterSeqId, int pageSize, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = Proc(conn, "dbo.usp_Document_List");
        cmd.Parameters.Add("@Category", SqlDbType.VarChar, 50).Value = (object?)category ?? DBNull.Value;
        cmd.Parameters.Add("@Search", SqlDbType.NVarChar, 100).Value = (object?)search ?? DBNull.Value;
        cmd.Parameters.Add("@AfterSeqId", SqlDbType.BigInt).Value = afterSeqId;
        cmd.Parameters.Add("@PageSize", SqlDbType.Int).Value = pageSize;

        await conn.OpenAsync(ct);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var items = new List<DocumentListItem>();
        while (await reader.ReadAsync(ct))
        {
            items.Add(new DocumentListItem(
                reader.GetInt64(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3),
                reader.GetInt32(4), reader.GetDateTime(5), reader.GetString(6), reader.GetInt64(7),
                reader.GetString(8)));
        }
        return items;
    }

    public async Task<DocumentDetails?> GetAsync(Guid documentId, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = Proc(conn, "dbo.usp_Document_Get");
        cmd.Parameters.Add("@DocumentId", SqlDbType.UniqueIdentifier).Value = documentId;

        await conn.OpenAsync(ct);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        // Result set 1: the document
        if (!await reader.ReadAsync(ct)) return null;
        var id = reader.GetGuid(0);
        var title = reader.GetString(1);
        var category = reader.GetString(2);
        var currentVersion = reader.GetInt32(3);
        var createdBy = reader.GetString(4);
        var createdOn = reader.GetDateTime(5);
        var updatedOn = reader.GetDateTime(6);

        // Result set 2: its versions
        var versions = new List<DocumentVersionInfo>();
        if (await reader.NextResultAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                versions.Add(new DocumentVersionInfo(
                    reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3),
                    reader.GetString(4), reader.GetString(5), reader.GetDateTime(6)));
            }
        }
        return new DocumentDetails(id, title, category, currentVersion, createdBy, createdOn, updatedOn, versions);
    }

    public async Task<DownloadInfo?> GetVersionForDownloadAsync(Guid documentId, int? versionNo, string user, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = Proc(conn, "dbo.usp_Document_GetVersionForDownload");
        cmd.Parameters.Add("@DocumentId", SqlDbType.UniqueIdentifier).Value = documentId;
        cmd.Parameters.Add("@VersionNo", SqlDbType.Int).Value = (object?)versionNo ?? DBNull.Value;
        cmd.Parameters.Add("@UserName", SqlDbType.VarChar, 100).Value = user;

        await conn.OpenAsync(ct);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new DownloadInfo(reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
                                reader.GetString(3), reader.GetInt64(4));
    }

    public async Task<bool> SoftDeleteAsync(Guid documentId, string user, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = Proc(conn, "dbo.usp_Document_SoftDelete");
        cmd.Parameters.Add("@DocumentId", SqlDbType.UniqueIdentifier).Value = documentId;
        cmd.Parameters.Add("@UserName", SqlDbType.VarChar, 100).Value = user;
        await conn.OpenAsync(ct);
        var affected = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(affected) == 1;
    }

    public async Task<(Guid DocumentId, int VersionNo)?> FindByHashAsync(string sha256, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = Proc(conn, "dbo.usp_Document_FindByHash");
        cmd.Parameters.Add("@Sha256", SqlDbType.Char, 64).Value = sha256;
        await conn.OpenAsync(ct);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return (reader.GetGuid(0), reader.GetInt32(1));
    }

    public async Task<IReadOnlyList<AuditEntry>> GetAuditAsync(Guid documentId, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = Proc(conn, "dbo.usp_AuditLog_ByDocument");
        cmd.Parameters.Add("@DocumentId", SqlDbType.UniqueIdentifier).Value = documentId;
        await conn.OpenAsync(ct);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<AuditEntry>();
        while (await reader.ReadAsync(ct))
        {
            list.Add(new AuditEntry(reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetInt32(1),
                reader.GetString(2), reader.GetDateTime(3)));
        }
        return list;
    }

    private static SqlCommand Proc(SqlConnection conn, string name) =>
        new(name, conn) { CommandType = CommandType.StoredProcedure, CommandTimeout = 30 };

    private static void AddFileParams(SqlCommand cmd, NewFile file, string user)
    {
        cmd.Parameters.Add("@BlobName", SqlDbType.VarChar, 300).Value = file.BlobName;
        cmd.Parameters.Add("@FileName", SqlDbType.NVarChar, 260).Value = file.FileName;
        cmd.Parameters.Add("@ContentType", SqlDbType.VarChar, 100).Value = file.ContentType;
        cmd.Parameters.Add("@SizeBytes", SqlDbType.BigInt).Value = file.SizeBytes;
        cmd.Parameters.Add("@Sha256", SqlDbType.Char, 64).Value = file.Sha256;
        cmd.Parameters.Add("@UserName", SqlDbType.VarChar, 100).Value = user;
    }
}
