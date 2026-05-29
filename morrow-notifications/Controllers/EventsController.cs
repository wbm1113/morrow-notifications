using Microsoft.AspNetCore.Mvc;
using MN.Interfaces;
using MN.Models;

namespace morrow_notifications.Controllers;

[ApiController]
[Route("api/events")]
public class EventsController(
    IIngestionService ingestionService,
    IRateLimiterService rateLimiterService) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Ingest([FromBody] IngestEventRequest request, CancellationToken ct)
    {
        if (request.TenantId == Guid.Empty)
            return BadRequest(new { error = "TenantId is required." });

        if (string.IsNullOrWhiteSpace(request.EventType))
            return BadRequest(new { error = "EventType is required." });

        if (string.IsNullOrWhiteSpace(request.Payload))
            return BadRequest(new { error = "Payload is required." });

        if (!rateLimiterService.IsKnownTenant(request.TenantId))
            return NotFound(new { error = $"Tenant '{request.TenantId}' not found." });

        using var lease = rateLimiterService.TryAcquire(request.TenantId);
        if (!lease.IsAcquired)
        {
            return StatusCode(429, new { error = "Rate limit exceeded for this tenant." });
        }

        try
        {
            var result = await ingestionService.IngestAsync(request, ct);
            return Accepted(result);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return UnprocessableEntity(new { error = ex.Message });
        }
    }
}
