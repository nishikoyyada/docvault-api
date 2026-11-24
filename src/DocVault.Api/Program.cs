using DocVault.Api.Data;
using DocVault.Api.Middleware;
using DocVault.Api.Options;
using DocVault.Api.Services;
using Microsoft.Data.SqlClient;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.SectionName));

builder.Services.AddSingleton<IBlobStorageService, BlobStorageService>();   // BlobServiceClient is thread-safe
builder.Services.AddScoped<IDocumentRepository, DocumentRepository>();
builder.Services.AddScoped<IDocumentService, DocumentService>();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    var xml = Path.Combine(AppContext.BaseDirectory, $"{typeof(Program).Assembly.GetName().Name}.xml");
    if (File.Exists(xml)) c.IncludeXmlComments(xml);
});

var app = builder.Build();

app.UseMiddleware<ExceptionHandlingMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapControllers();

// Simple health endpoint that checks both SQL Server and Blob Storage
app.MapGet("/health", async (IConfiguration config, IBlobStorageService blobs, CancellationToken ct) =>
{
    try
    {
        await using var conn = new SqlConnection(config.GetConnectionString("DocVaultDb"));
        await conn.OpenAsync(ct);
        await blobs.EnsureContainerAsync(ct);
        return Results.Ok(new { status = "Healthy" });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable, title: "Unhealthy");
    }
});

// Create the blob container on startup if it doesn't exist
try
{
    await app.Services.GetRequiredService<IBlobStorageService>().EnsureContainerAsync(CancellationToken.None);
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "Blob storage not reachable at startup. Check Storage:ConnectionString.");
}

app.Run();

public partial class Program { }
