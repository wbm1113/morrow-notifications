namespace MN.Models;

public record CreateTenantRequest(string Name, int RateLimitPerMinute = 60);

public record UpdateTenantRequest(string? Name, int? RateLimitPerMinute, bool? IsActive);

public record TenantResponse(
    Guid Id,
    string Name,
    bool IsActive,
    int RateLimitPerMinute,
    DateTime CreatedAt);
