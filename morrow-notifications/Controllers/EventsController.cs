using Microsoft.AspNetCore.Mvc;
using MN.BusinessLogic;
using MN.Core;
using MN.Interfaces;
using MN.Models;

namespace morrow_notifications.Controllers;

[ApiController]
[Route("api/events")]
public class EventsController(IIngestionService ingestionService) : ControllerBase
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

        if (request.Payload.Length > NotificationLimits.MaxPayloadLength)
            return BadRequest(new { error = $"Payload exceeds maximum length of {NotificationLimits.MaxPayloadLength} characters." });

        try
        {
            // -> IngestionService
            var result = await ingestionService.IngestAsync(request, ct);
            return Accepted(result);
        }
        catch (RateLimitExceededException ex)
        {
            return StatusCode(429, new { error = ex.Message });
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
