using Microsoft.AspNetCore.Mvc;
using MN.Interfaces;
using MN.Models;

namespace morrow_notifications.Controllers;

[ApiController]
[Route("api/dead-letters")]
public class DeadLettersController(IDeadLetterQueue deadLetterQueue) : ControllerBase
{
    // Intentionally un-scoped: admin/debugging surface for inspecting all dead letters
    // regardless of tenant. No auth is in scope for this project.
    [HttpGet]
    public IActionResult GetAll() =>
        Ok(deadLetterQueue.GetAll().Select(ToResponse));

    [HttpGet("tenant/{tenantId:guid}")]
    public IActionResult GetByTenant(Guid tenantId) =>
        Ok(deadLetterQueue.GetByTenant(tenantId).Select(ToResponse));

    private static DeadLetterResponse ToResponse(MN.Models.DeadLetterMessage d) =>
        new(d.Id, d.TenantId, d.OriginalMessageId, d.DispatchId, d.ChannelType, d.EventType, d.Payload, d.FailureReason, d.FailedAt);
}
