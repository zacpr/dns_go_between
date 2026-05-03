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
        catch (UnauthorizedAccessException)
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                CreateSafeProblem(
                    StatusCodes.Status403Forbidden,
                    "Access denied",
                    "The requested zone is not allowed."));
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
        var target = BuildAuditTarget(request.ZoneName, request.HostName, request.RecordType.ToString());

        try
        {
            await _dns.AddRecordAsync(request, ct);
            _audit.LogWrite(user, "AddRecord", target, success: true, correlationId: correlationId);
            return Created($"/api/zones/{request.ZoneName}/records", null);
        }
        catch (UnauthorizedAccessException ex)
        {
            _audit.LogWrite(user, "AddRecord", target, success: false, ex.GetType().Name, correlationId);
            return StatusCode(
                StatusCodes.Status403Forbidden,
                CreateSafeProblem(
                    StatusCodes.Status403Forbidden,
                    "Access denied",
                    "You are not allowed to modify this zone."));
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
        {
            _audit.LogWrite(user, "AddRecord", target, success: false, ex.GetType().Name, correlationId);
            return BadRequest(CreateSafeProblem(
                StatusCodes.Status400BadRequest,
                "Invalid request",
                "The record request is invalid."));
        }
        catch (InvalidOperationException ex) when (IsDuplicateRecordAdd(ex))
        {
            _audit.LogWrite(user, "AddRecord", target, success: false, ex.GetType().Name, correlationId);
            return Conflict(CreateSafeProblem(
                StatusCodes.Status409Conflict,
                "Record already exists",
                "A matching DNS record already exists."));
        }
        catch (Exception ex)
        {
            _audit.LogWrite(user, "AddRecord", target, success: false, ex.GetType().Name, correlationId);
            _logger.LogError(ex, "Unexpected error adding record {Target}", target);
            return StatusCode(StatusCodes.Status500InternalServerError,
                CreateSafeProblem(
                    StatusCodes.Status500InternalServerError,
                    "Server error",
                    "An unexpected error occurred."));
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
        var target = BuildAuditTarget(request.ZoneName, request.HostName, request.RecordType.ToString());

        try
        {
            await _dns.DeleteRecordAsync(request, ct);
            _audit.LogWrite(user, "DeleteRecord", target, success: true, correlationId: correlationId);
            return NoContent();
        }
        catch (UnauthorizedAccessException ex)
        {
            _audit.LogWrite(user, "DeleteRecord", target, success: false, ex.GetType().Name, correlationId);
            return StatusCode(
                StatusCodes.Status403Forbidden,
                CreateSafeProblem(
                    StatusCodes.Status403Forbidden,
                    "Access denied",
                    "You are not allowed to modify this zone."));
        }
        catch (InvalidOperationException ex)
        {
            _audit.LogWrite(user, "DeleteRecord", target, success: false, ex.GetType().Name, correlationId);
            return NotFound(CreateSafeProblem(
                StatusCodes.Status404NotFound,
                "Record not found",
                "The record could not be found."));
        }
        catch (ArgumentException ex)
        {
            _audit.LogWrite(user, "DeleteRecord", target, success: false, ex.GetType().Name, correlationId);
            return BadRequest(CreateSafeProblem(
                StatusCodes.Status400BadRequest,
                "Invalid request",
                "The record request is invalid."));
        }
        catch (Exception ex)
        {
            _audit.LogWrite(user, "DeleteRecord", target, success: false, ex.GetType().Name, correlationId);
            _logger.LogError(ex, "Unexpected error deleting record {Target}", target);
            return StatusCode(StatusCodes.Status500InternalServerError,
                CreateSafeProblem(
                    StatusCodes.Status500InternalServerError,
                    "Server error",
                    "An unexpected error occurred."));
        }
    }

    private ProblemDetails CreateSafeProblem(int status, string title, string detail)
    {
        return new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail,
            Extensions = { ["correlationId"] = HttpContext.TraceIdentifier }
        };
    }

    private static string BuildAuditTarget(string zoneName, string hostName, string recordType)
    {
        return $"{recordType}:{hostName}@{zoneName}";
    }

    private static bool IsDuplicateRecordAdd(InvalidOperationException ex)
    {
        if (string.IsNullOrWhiteSpace(ex.Message))
        {
            return false;
        }

        return ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("record exists", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("resource record", StringComparison.OrdinalIgnoreCase) &&
               ex.Message.Contains("exist", StringComparison.OrdinalIgnoreCase);
    }
}
