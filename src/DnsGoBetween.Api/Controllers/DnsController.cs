using DnsGoBetween.Core.Interfaces;
using DnsGoBetween.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DnsGoBetween.Api.Controllers;

[ApiController]
[Route("api")]
[Authorize]
public sealed class DnsController : ControllerBase
{
    private readonly IDnsRecordService _dns;
    private readonly IAuditLogger _audit;
    private readonly ILogger<DnsController> _logger;

    public DnsController(
        IDnsRecordService dns,
        IAuditLogger audit,
        ILogger<DnsController> logger)
    {
        _dns = dns;
        _audit = audit;
        _logger = logger;
    }

    /// <summary>Returns all DNS zones (filtered to the allowlist if configured).</summary>
    [HttpGet("zones")]
    [Authorize(Policy = "ReadPolicy")]
    public async Task<IActionResult> GetZones(CancellationToken ct)
    {
        var zones = await _dns.ListZonesAsync(ct);
        return Ok(zones);
    }

    /// <summary>Returns all resource records in a zone, optionally filtered to a hostname.</summary>
    [HttpGet("zones/{zone}/records")]
    [Authorize(Policy = "ReadPolicy")]
    public async Task<IActionResult> GetRecords(
        string zone, [FromQuery] string? node, CancellationToken ct)
    {
        try
        {
            var records = await _dns.ListRecordsAsync(zone, node, ct);
            return Ok(records);
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                new ProblemDetails { Detail = ex.Message });
        }
    }

    /// <summary>Creates a DNS resource record.</summary>
    [HttpPost("records")]
    [Authorize(Policy = "WritePolicy")]
    public async Task<IActionResult> AddRecord(
        [FromBody] AddRecordRequest request, CancellationToken ct)
    {
        var correlationId = HttpContext.TraceIdentifier;
        var user = User.Identity?.Name ?? "unknown";
        var target = $"{request.RecordType}:{request.HostName}@{request.ZoneName}={request.Data}";

        try
        {
            await _dns.AddRecordAsync(request, ct);
            _audit.LogWrite(user, "AddRecord", target, success: true, correlationId: correlationId);
            return Created($"/api/zones/{request.ZoneName}/records", null);
        }
        catch (UnauthorizedAccessException ex)
        {
            _audit.LogWrite(user, "AddRecord", target, success: false, ex.Message, correlationId);
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails { Detail = ex.Message });
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
        {
            _audit.LogWrite(user, "AddRecord", target, success: false, ex.Message, correlationId);
            return BadRequest(new ProblemDetails { Detail = ex.Message });
        }
        catch (Exception ex)
        {
            _audit.LogWrite(user, "AddRecord", target, success: false, ex.Message, correlationId);
            _logger.LogError(ex, "Unexpected error adding record {Target}", target);
            return StatusCode(StatusCodes.Status500InternalServerError,
                new ProblemDetails { Detail = "An unexpected error occurred." });
        }
    }

    /// <summary>Deletes a DNS resource record (exact-match required).</summary>
    [HttpDelete("records")]
    [Authorize(Policy = "WritePolicy")]
    public async Task<IActionResult> DeleteRecord(
        [FromBody] DeleteRecordRequest request, CancellationToken ct)
    {
        var correlationId = HttpContext.TraceIdentifier;
        var user = User.Identity?.Name ?? "unknown";
        var target = $"{request.RecordType}:{request.HostName}@{request.ZoneName}={request.Data}";

        try
        {
            await _dns.DeleteRecordAsync(request, ct);
            _audit.LogWrite(user, "DeleteRecord", target, success: true, correlationId: correlationId);
            return NoContent();
        }
        catch (UnauthorizedAccessException ex)
        {
            _audit.LogWrite(user, "DeleteRecord", target, success: false, ex.Message, correlationId);
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails { Detail = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            _audit.LogWrite(user, "DeleteRecord", target, success: false, ex.Message, correlationId);
            return NotFound(new ProblemDetails { Detail = ex.Message });
        }
        catch (ArgumentException ex)
        {
            _audit.LogWrite(user, "DeleteRecord", target, success: false, ex.Message, correlationId);
            return BadRequest(new ProblemDetails { Detail = ex.Message });
        }
        catch (Exception ex)
        {
            _audit.LogWrite(user, "DeleteRecord", target, success: false, ex.Message, correlationId);
            _logger.LogError(ex, "Unexpected error deleting record {Target}", target);
            return StatusCode(StatusCodes.Status500InternalServerError,
                new ProblemDetails { Detail = "An unexpected error occurred." });
        }
    }
}
