using Microsoft.EntityFrameworkCore;
using MN.DAL;
using MN.Entities;

namespace MN.Tests;

public class OutboxClaimTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"mn-outbox-claim-{Guid.NewGuid():N}.db");
    private readonly TenantContext _tenantContext = new();

    [Fact]
    public async Task TryClaim_sameRow_onlyOnePublisherClaimsIt()
    {
        await SeedOutboxRowsAsync(1);

        var claims = await Task.WhenAll(
            TryClaimAsync("publisher-a", max: 1),
            TryClaimAsync("publisher-b", max: 1),
            TryClaimAsync("publisher-c", max: 1));

        var allClaimed = claims.SelectMany(c => c).ToList();
        Assert.Single(allClaimed);
    }

    [Fact]
    public async Task MarkPublished_onlyHonorsMatchingClaimer()
    {
        await SeedOutboxRowsAsync(1);

        var claimed = await TryClaimAsync("owner", max: 1);
        Assert.Single(claimed);

        await using var context = CreateContext();
        var repo = new DispatchOutboxRepository(context);
        await repo.MarkPublishedAsync([claimed[0].Id], "other-publisher", CancellationToken.None);

        var row = await context.DispatchOutbox.SingleAsync(o => o.Id == claimed[0].Id);
        Assert.Null(row.PublishedAt);
        Assert.Equal("owner", row.ClaimedBy);
    }

    [Fact]
    public async Task TryClaim_batchClaimsUpToMaxRows()
    {
        await SeedOutboxRowsAsync(5);

        var claimed = await TryClaimAsync("publisher-a", max: 50);
        Assert.Equal(5, claimed.Count);
    }

    private async Task SeedOutboxRowsAsync(int count)
    {
        var tenantId = Guid.NewGuid();
        _tenantContext.CurrentTenantId = tenantId;
        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync();
        for (var i = 0; i < count; i++)
        {
            context.DispatchOutbox.Add(new DispatchOutboxEntry
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                OriginalMessageId = Guid.NewGuid(),
                RuleId = Guid.NewGuid(),
                ChannelType = "slack",
                SchemaVersion = 1,
                EventType = "test.event",
                Payload = "{}",
                MessageCreatedAt = DateTime.UtcNow.AddMilliseconds(-count + i),
                CreatedAt = DateTime.UtcNow.AddMilliseconds(-count + i)
            });
        }

        await context.SaveChangesAsync();
    }

    private async Task<IReadOnlyList<DispatchOutboxEntry>> TryClaimAsync(string claimerId, int max)
    {
        await using var context = CreateContext();
        var repo = new DispatchOutboxRepository(context);
        return await repo.TryClaimUnpublishedAcrossTenantsAsync(max, claimerId, CancellationToken.None);
    }

    private AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        return new AppDbContext(options, _tenantContext);
    }

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-shm", "-wal" })
        {
            var path = _dbPath + suffix;
            if (File.Exists(path))
                try { File.Delete(path); } catch { /* best effort */ }
        }
    }
}
