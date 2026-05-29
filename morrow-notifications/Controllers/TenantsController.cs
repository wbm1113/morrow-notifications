using Microsoft.AspNetCore.Mvc;
using MN.Entities;
using MN.Interfaces;
using MN.Models;

namespace morrow_notifications.Controllers;

[ApiController]
[Route("api/tenants")]
public class TenantsController(
    ITenantRepository tenantRepository,
    IRateLimiterService rateLimiterService) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct)
    {
        var tenants = await tenantRepository.GetAllAsync(ct);
        return Ok(tenants.Select(ToResponse));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var tenant = await tenantRepository.GetByIdAsync(id, ct);
        return tenant is null ? NotFound() : Ok(ToResponse(tenant));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateTenantRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "Name is required." });

        if (request.RateLimitPerMinute <= 0)
            return BadRequest(new { error = "RateLimitPerMinute must be > 0." });

        var name = request.Name.Trim();
        var existing = await tenantRepository.GetByNameAsync(name, ct);
        if (existing is not null)
            return Conflict(new { error = $"A tenant named '{existing.Name}' already exists.", id = existing.Id });

        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = name,
            RateLimitPerMinute = request.RateLimitPerMinute,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var created = await tenantRepository.CreateAsync(tenant, ct);
        rateLimiterService.ConfigureTenant(created.Id, created.RateLimitPerMinute);

        return CreatedAtAction(nameof(GetById), new { id = created.Id }, ToResponse(created));
    }

    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateTenantRequest request, CancellationToken ct)
    {
        var tenant = await tenantRepository.GetByIdAsync(id, ct);
        if (tenant is null) return NotFound();

        if (request.Name is not null) tenant.Name = request.Name.Trim();
        if (request.IsActive is not null) tenant.IsActive = request.IsActive.Value;
        if (request.RateLimitPerMinute is not null)
        {
            if (request.RateLimitPerMinute <= 0)
                return BadRequest(new { error = "RateLimitPerMinute must be > 0." });
            tenant.RateLimitPerMinute = request.RateLimitPerMinute.Value;
        }

        var updated = await tenantRepository.UpdateAsync(tenant, ct);

        // Keep limiter state in sync: reconfigure on active, remove on deactivate
        if (updated.IsActive)
            rateLimiterService.ConfigureTenant(updated.Id, updated.RateLimitPerMinute);
        else
            rateLimiterService.RemoveTenant(updated.Id);

        return Ok(ToResponse(updated));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var tenant = await tenantRepository.GetByIdAsync(id, ct);
        if (tenant is null) return NotFound();

        await tenantRepository.DeleteAsync(id, ct);
        rateLimiterService.RemoveTenant(id);
        return NoContent();
    }

    private static TenantResponse ToResponse(Tenant t) =>
        new(t.Id, t.Name, t.IsActive, t.RateLimitPerMinute, t.CreatedAt);
}
