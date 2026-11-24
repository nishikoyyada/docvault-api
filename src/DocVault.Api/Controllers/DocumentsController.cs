using DocVault.Api.Models;
using DocVault.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace DocVault.Api.Controllers;

[ApiController]
[Route("api/documents")]
[Produces("application/json")]
public sealed class DocumentsController(IDocumentService service) : ControllerBase
{
    private const long MaxRequestBytes = 30 * 1024 * 1024;

    /// <summary>Upload a new document (version 1).</summary>
    [HttpPost]
    [RequestSizeLimit(MaxRequestBytes)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(UploadResult), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Upload([FromForm] UploadDocumentRequest request, CancellationToken ct)
    {
        var result = await service.UploadAsync(request, CurrentUser, ct);
        return CreatedAtAction(nameof(Get), new { id = result.DocumentId }, result);
    }

    /// <summary>Upload a new version of an existing document.</summary>
    [HttpPost("{id:guid}/versions")]
    [RequestSizeLimit(MaxRequestBytes)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(UploadResult), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AddVersion(Guid id, [FromForm] UploadVersionRequest request, CancellationToken ct)
    {
        var result = await service.AddVersionAsync(id, request.File, CurrentUser, ct);
        return CreatedAtAction(nameof(Get), new { id }, result);
    }

    /// <summary>List documents. Use nextCursor from the response as afterSeqId for the next page.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<DocumentListItem>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] string? category, [FromQuery] string? search,
        [FromQuery] long afterSeqId = 0, [FromQuery] int pageSize = 20, CancellationToken ct = default)
        => Ok(await service.ListAsync(category, search, afterSeqId, pageSize, ct));

    /// <summary>Get a document with all its versions.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(DocumentDetails), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Ok(await service.GetAsync(id, ct));

    /// <summary>Stream the file through the API (latest version if none is given).</summary>
    [HttpGet("{id:guid}/download")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Download(Guid id, [FromQuery] int? version, CancellationToken ct)
    {
        var (content, info) = await service.DownloadAsync(id, version, CurrentUser, ct);
        return File(content, info.ContentType, info.FileName, enableRangeProcessing: true);
    }

    /// <summary>Get a short-lived, read-only link straight to Blob Storage (no API bandwidth used).</summary>
    [HttpGet("{id:guid}/download-link")]
    [ProducesResponseType(typeof(DownloadLink), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetDownloadLink(Guid id, [FromQuery] int? version, CancellationToken ct)
        => Ok(await service.GetDownloadLinkAsync(id, version, CurrentUser, ct));

    /// <summary>Soft-delete a document.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await service.DeleteAsync(id, CurrentUser, ct);
        return NoContent();
    }

    /// <summary>Who uploaded, downloaded or deleted this document, and when.</summary>
    [HttpGet("{id:guid}/audit")]
    [ProducesResponseType(typeof(IReadOnlyList<AuditEntry>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Audit(Guid id, CancellationToken ct) => Ok(await service.GetAuditAsync(id, ct));

    // Demo only: user comes from a header. In production, read it from the JWT (User.Identity.Name).
    private string CurrentUser
    {
        get
        {
            var name = Request.Headers["X-User-Name"].ToString().Trim();
            if (string.IsNullOrEmpty(name)) return "anonymous";
            return name.Length > 100 ? name[..100] : name;
        }
    }
}
