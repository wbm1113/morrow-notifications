using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MN.Tests;

/// <summary>
/// Verifies the three core tenant isolation guarantees:
///   1. Tenant B cannot see Tenant A's routing rules (data isolation).
///   2. Tenant B's events are not dispatched by Tenant A's rules (dispatch isolation).
///   3. Tenant A exhausting its rate limit does not block Tenant B (resource isolation).
/// </summary>
public class TenantIsolationTests(CustomWebApplicationFactory factory)
    : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task TenantB_CannotSee_TenantAs_Rules()
    {
        var tenantA = await CreateTenantAsync(100);
        await CreateRuleAsync(tenantA, "order.created", "slack");

        var tenantB = await CreateTenantAsync(100);

        var response = await _client.GetAsync($"/api/tenants/{tenantB}/rules");
        response.EnsureSuccessStatusCode();

        var rules = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.Empty(rules!);
    }

    [Fact]
    public async Task TenantB_Event_IsDeadLettered_WhenOnlyTenantA_HasMatchingRule()
    {
        // Tenant A has a rule for "order.created"; Tenant B has none.
        var tenantA = await CreateTenantAsync(100);
        await CreateRuleAsync(tenantA, "order.created", "slack");

        var tenantB = await CreateTenantAsync(100);

        var ingestResponse = await IngestEventAsync(tenantB, "order.created", new { orderId = "x" });
        Assert.Equal(HttpStatusCode.Accepted, ingestResponse.StatusCode);

        // Background processor must dead-letter the message (no rule matched for Tenant B).
        var deadLetters = await PollDeadLettersAsync(tenantB, expectedCount: 1, TimeSpan.FromSeconds(5));
        Assert.Single(deadLetters);
        Assert.Contains(
            "No matching routing rules",
            deadLetters[0].GetProperty("failureReason").GetString());
    }

    [Fact]
    public async Task TenantA_ExhaustingRateLimit_DoesNotBlock_TenantB()
    {
        var tenantA = await CreateTenantAsync(rateLimitPerMinute: 1);
        var tenantB = await CreateTenantAsync(rateLimitPerMinute: 100);

        var first = await IngestEventAsync(tenantA, "ping", new { });
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);

        var second = await IngestEventAsync(tenantA, "ping", new { });
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);

        // Tenant B's own limiter is untouched.
        var forB = await IngestEventAsync(tenantB, "ping", new { });
        Assert.Equal(HttpStatusCode.Accepted, forB.StatusCode);
    }

    // --- helpers ---

    private async Task<Guid> CreateTenantAsync(int rateLimitPerMinute)
    {
        var response = await _client.PostAsJsonAsync("/api/tenants", new
        {
            name = $"Test-Tenant-{Guid.NewGuid():N}",
            rateLimitPerMinute
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetGuid();
    }

    private async Task CreateRuleAsync(Guid tenantId, string eventType, string channelType)
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/tenants/{tenantId}/rules",
            new { eventType, channelType });
        response.EnsureSuccessStatusCode();
    }

    private Task<HttpResponseMessage> IngestEventAsync(Guid tenantId, string eventType, object payload) =>
        _client.PostAsJsonAsync("/api/events", new
        {
            tenantId,
            eventType,
            payload = JsonSerializer.Serialize(payload)  // Payload field is string on the server
        });

    private async Task<List<JsonElement>> PollDeadLettersAsync(
        Guid tenantId, int expectedCount, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var response = await _client.GetAsync($"/api/dead-letters/tenant/{tenantId}");
            var items = await response.Content.ReadFromJsonAsync<JsonElement[]>();
            if (items?.Length >= expectedCount)
                return [.. items.Take(expectedCount)];
            await Task.Delay(100);
        }
        return [];
    }
}
