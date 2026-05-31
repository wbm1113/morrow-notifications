using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MN.BusinessLogic;
using MN.Core;
using MN.Entities;
using MN.Interfaces;
using MN.Models;

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
        var tenantA = await CreateTenantAsync(100);
        await CreateRuleAsync(tenantA, "order.created", "slack");

        var tenantB = await CreateTenantAsync(100);

        var ingestResponse = await IngestEventAsync(tenantB, "order.created", new { orderId = "x" });
        Assert.Equal(HttpStatusCode.Accepted, ingestResponse.StatusCode);

        var deadLetters = await PollDeadLettersAsync(tenantB, expectedCount: 1, TimeSpan.FromSeconds(5));
        Assert.Single(deadLetters);
        Assert.Equal(JsonValueKind.Null, deadLetters[0].GetProperty("dispatchId").ValueKind);
        Assert.Contains(
            "No matching routing rules",
            deadLetters[0].GetProperty("failureReason").GetString());
    }

    [Fact]
    public async Task EventRoutingProcessor_ScopesCorrectTenantContext_PerGroupInBatch()
    {
        var tenantA = await CreateTenantAsync(100);
        await CreateRuleAsync(tenantA, "order.created", "slack");

        var tenantB = await CreateTenantAsync(100);

        await IngestEventAsync(tenantA, "order.created", new { });
        await IngestEventAsync(tenantB, "order.created", new { });

        var tenantBDeadLetters = await PollDeadLettersAsync(tenantB, expectedCount: 1, TimeSpan.FromSeconds(5));
        Assert.Single(tenantBDeadLetters);

        var response = await _client.GetAsync($"/api/dead-letters/tenant/{tenantA}");
        var items = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.Empty(items!);
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

        var forB = await IngestEventAsync(tenantB, "ping", new { });
        Assert.Equal(HttpStatusCode.Accepted, forB.StatusCode);
    }

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
            payload = JsonSerializer.Serialize(payload)
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

public class PartialDispatchFailureTests : IClassFixture<PartialFailureWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly PartialFailureWebApplicationFactory _factory;

    public PartialDispatchFailureTests(PartialFailureWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
        _factory = factory;
    }

    [Fact]
    public async Task OneChannelFailure_DeadLettersOnlyFailedDispatch()
    {
        var (tenantId, messageId) = await IngestMultiChannelEventAsync();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        JsonElement[]? deadLetters = null;
        while (DateTime.UtcNow < deadline)
        {
            var response = await _client.GetAsync($"/api/dead-letters/tenant/{tenantId}");
            deadLetters = await response.Content.ReadFromJsonAsync<JsonElement[]>();
            if (deadLetters?.Length >= 1)
                break;
            await Task.Delay(100);
        }

        Assert.NotNull(deadLetters);
        var channelFailure = Assert.Single(deadLetters!);
        Assert.Equal("teams", channelFailure.GetProperty("channelType").GetString());
        Assert.NotEqual(Guid.Empty, channelFailure.GetProperty("dispatchId").GetGuid());

        var status = await GetParentMessageStatusAsync(tenantId, messageId);
        Assert.Equal(MessageStatus.PartiallyDispatched, status);
    }

    private async Task<(Guid TenantId, Guid MessageId)> IngestMultiChannelEventAsync()
    {
        var tenantResponse = await _client.PostAsJsonAsync("/api/tenants", new
        {
            name = $"Partial-Fail-{Guid.NewGuid():N}",
            rateLimitPerMinute = 100
        });
        tenantResponse.EnsureSuccessStatusCode();
        var tenantId = (await tenantResponse.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetGuid();

        await _client.PostAsJsonAsync($"/api/tenants/{tenantId}/rules",
            new { eventType = "order.created", channelType = "slack" });
        await _client.PostAsJsonAsync($"/api/tenants/{tenantId}/rules",
            new { eventType = "order.created", channelType = "teams" });

        var ingest = await _client.PostAsJsonAsync("/api/events", new
        {
            tenantId,
            eventType = "order.created",
            payload = JsonSerializer.Serialize(new { orderId = "x" })
        });
        Assert.Equal(HttpStatusCode.Accepted, ingest.StatusCode);
        var messageId = (await ingest.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("messageId").GetGuid();

        return (tenantId, messageId);
    }

    private async Task<MessageStatus> GetParentMessageStatusAsync(Guid tenantId, Guid messageId)
    {
        using var scope = _factory.Services.CreateScope();
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenantContext.CurrentTenantId = tenantId;
        var messageRepo = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
        var message = await messageRepo.GetByIdAsync(tenantId, messageId, CancellationToken.None);
        Assert.NotNull(message);
        return message!.Status;
    }
}

public class AllDispatchFailureTests : IClassFixture<AllFailureWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly AllFailureWebApplicationFactory _factory;

    public AllDispatchFailureTests(AllFailureWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
        _factory = factory;
    }

    [Fact]
    public async Task AllChannelsFail_ParentMessageIsDeliveryFailed()
    {
        var tenantResponse = await _client.PostAsJsonAsync("/api/tenants", new
        {
            name = $"All-Fail-{Guid.NewGuid():N}",
            rateLimitPerMinute = 100
        });
        tenantResponse.EnsureSuccessStatusCode();
        var tenantId = (await tenantResponse.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetGuid();

        await _client.PostAsJsonAsync($"/api/tenants/{tenantId}/rules",
            new { eventType = "order.created", channelType = "slack" });
        await _client.PostAsJsonAsync($"/api/tenants/{tenantId}/rules",
            new { eventType = "order.created", channelType = "teams" });

        var ingest = await _client.PostAsJsonAsync("/api/events", new
        {
            tenantId,
            eventType = "order.created",
            payload = JsonSerializer.Serialize(new { orderId = "x" })
        });
        Assert.Equal(HttpStatusCode.Accepted, ingest.StatusCode);
        var messageId = (await ingest.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("messageId").GetGuid();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var response = await _client.GetAsync($"/api/dead-letters/tenant/{tenantId}");
            var deadLetters = await response.Content.ReadFromJsonAsync<JsonElement[]>();
            if (deadLetters?.Length >= 2)
                break;
            await Task.Delay(100);
        }

        using var scope = _factory.Services.CreateScope();
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenantContext.CurrentTenantId = tenantId;
        var messageRepo = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
        var message = await messageRepo.GetByIdAsync(tenantId, messageId, CancellationToken.None);

        Assert.NotNull(message);
        Assert.Equal(MessageStatus.DeliveryFailed, message!.Status);
    }
}

public sealed class PartialFailureWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"mn-partial-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration(config =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = $"Data Source={_dbPath}"
            }));

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<INotificationChannel>();
            services.AddSingleton<INotificationChannel, SlackChannel>();
            services.AddSingleton<INotificationChannel, FailingTeamsChannel>();
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        foreach (var suffix in new[] { "", "-shm", "-wal" })
        {
            var path = _dbPath + suffix;
            if (File.Exists(path))
                try { File.Delete(path); } catch { }
        }
    }
}

public sealed class AllFailureWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"mn-allfail-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration(config =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = $"Data Source={_dbPath}"
            }));

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<INotificationChannel>();
            services.AddSingleton<INotificationChannel, FailingSlackChannel>();
            services.AddSingleton<INotificationChannel, FailingTeamsChannel>();
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        foreach (var suffix in new[] { "", "-shm", "-wal" })
        {
            var path = _dbPath + suffix;
            if (File.Exists(path))
                try { File.Delete(path); } catch { }
        }
    }
}

internal sealed class FailingSlackChannel : INotificationChannel
{
    public string ChannelType => ChannelTypes.Slack;

    public Task SendAsync(DispatchMessage dispatch, RoutingRule rule, CancellationToken ct) =>
        throw new InvalidOperationException("Simulated Slack channel failure.");
}

internal sealed class FailingTeamsChannel : INotificationChannel
{
    public string ChannelType => ChannelTypes.Teams;

    public Task SendAsync(DispatchMessage dispatch, RoutingRule rule, CancellationToken ct) =>
        throw new InvalidOperationException("Simulated Teams channel failure.");
}
