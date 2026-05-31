using Microsoft.AspNetCore.Mvc;
using MN.Entities;
using MN.Interfaces;
using MN.Models;

namespace morrow_notifications.Controllers;

[ApiController]
[Route("api/tenants/{tenantId:guid}/rules")]
public class RoutingRulesController(
    ITenantRepository tenantRepository,
    IRoutingRuleRepository ruleRepository,
    ITenantContext tenantContext,
    IEnumerable<INotificationChannel> channels) : ControllerBase
{
    // Valid channel types are derived from registered INotificationChannel implementations.
    // Adding a new channel class is the only change needed — no edits here.
    private readonly HashSet<string> _validChannelTypes =
        new(channels.Select(c => c.ChannelType), StringComparer.OrdinalIgnoreCase);
        
    [HttpGet]
    public async Task<IActionResult> GetAll(Guid tenantId, CancellationToken ct)
    {
        tenantContext.CurrentTenantId = tenantId;
        if (!await tenantRepository.ExistsAsync(tenantId, ct)) return NotFound(new { error = "Tenant not found." });
        var rules = await ruleRepository.GetByTenantAsync(tenantId, ct);
        return Ok(rules.Select(ToResponse));
    }

    [HttpGet("{ruleId:guid}")]
    public async Task<IActionResult> GetById(Guid tenantId, Guid ruleId, CancellationToken ct)
    {
        tenantContext.CurrentTenantId = tenantId;
        if (!await tenantRepository.ExistsAsync(tenantId, ct)) return NotFound(new { error = "Tenant not found." });
        var rule = await ruleRepository.GetByIdAsync(tenantId, ruleId, ct);
        return rule is null ? NotFound() : Ok(ToResponse(rule));
    }

    [HttpPost]
    public async Task<IActionResult> Create(Guid tenantId, [FromBody] CreateRoutingRuleRequest request, CancellationToken ct)
    {
        tenantContext.CurrentTenantId = tenantId;
        if (!await tenantRepository.ExistsAsync(tenantId, ct)) return NotFound(new { error = "Tenant not found." });

        if (string.IsNullOrWhiteSpace(request.EventType))
            return BadRequest(new { error = "EventType is required." });

        if (string.IsNullOrWhiteSpace(request.ChannelType))
            return BadRequest(new { error = "ChannelType is required." });

        if (!_validChannelTypes.Contains(request.ChannelType))
            return BadRequest(new { error = $"ChannelType must be one of: {string.Join(", ", _validChannelTypes)}." });

        var eventType = request.EventType.Trim();
        var channelType = request.ChannelType.ToLowerInvariant();
        if (await ruleRepository.GetByEventTypeAndChannelAsync(tenantId, eventType, channelType, ct) is not null)
        {
            return Conflict(new
            {
                error = $"A routing rule for event type '{eventType}' and channel '{channelType}' already exists for this tenant."
            });
        }

        var rule = new RoutingRule
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            EventType = eventType,
            ChannelType = channelType,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var created = await ruleRepository.CreateAsync(rule, ct);
        return CreatedAtAction(nameof(GetById), new { tenantId, ruleId = created.Id }, ToResponse(created));
    }

    [HttpPatch("{ruleId:guid}")]
    public async Task<IActionResult> Update(Guid tenantId, Guid ruleId, [FromBody] UpdateRoutingRuleRequest request, CancellationToken ct)
    {
        tenantContext.CurrentTenantId = tenantId;
        if (!await tenantRepository.ExistsAsync(tenantId, ct)) return NotFound(new { error = "Tenant not found." });

        var rule = await ruleRepository.GetByIdAsync(tenantId, ruleId, ct);
        if (rule is null) return NotFound();

        if (request.ChannelType is not null && !_validChannelTypes.Contains(request.ChannelType))
            return BadRequest(new { error = $"ChannelType must be one of: {string.Join(", ", _validChannelTypes)}." });

        var eventType = request.EventType?.Trim() ?? rule.EventType;
        var channelType = request.ChannelType?.ToLowerInvariant() ?? rule.ChannelType;

        if (request.EventType is not null || request.ChannelType is not null)
        {
            var existing = await ruleRepository.GetByEventTypeAndChannelAsync(tenantId, eventType, channelType, ct);
            if (existing is not null && existing.Id != ruleId)
            {
                return Conflict(new
                {
                    error = $"A routing rule for event type '{eventType}' and channel '{channelType}' already exists for this tenant."
                });
            }
        }

        if (request.EventType is not null) rule.EventType = eventType;
        if (request.ChannelType is not null) rule.ChannelType = channelType;
        if (request.IsActive is not null) rule.IsActive = request.IsActive.Value;

        var updated = await ruleRepository.UpdateAsync(rule, ct);
        return Ok(ToResponse(updated));
    }

    [HttpDelete("{ruleId:guid}")]
    public async Task<IActionResult> Delete(Guid tenantId, Guid ruleId, CancellationToken ct)
    {
        tenantContext.CurrentTenantId = tenantId;
        if (!await tenantRepository.ExistsAsync(tenantId, ct)) return NotFound(new { error = "Tenant not found." });
        var rule = await ruleRepository.GetByIdAsync(tenantId, ruleId, ct);
        if (rule is null) return NotFound();

        await ruleRepository.DeleteAsync(tenantId, ruleId, ct);
        return NoContent();
    }

    private static RoutingRuleResponse ToResponse(RoutingRule r) =>
        new(r.Id, r.TenantId, r.EventType, r.ChannelType, r.IsActive, r.CreatedAt);
}
