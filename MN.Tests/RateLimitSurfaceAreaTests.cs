using System.Reflection;
using MN.Core;

namespace MN.Tests;

// the idea here is to just force devs to think about rate limiters
// when adding new channels

public class RateLimitSurfaceAreaTests
{
    [Fact]
    public void SharedRateLimiter_surfaceArea_mustNotExceedConfiguredMaximumWhenChannelsGrow()
    {
        var channelCount = CountChannelTypeMembers();
        var surfaceArea = channelCount * RateLimitConstants.PermitsConsumedPerChannel;

        if (surfaceArea > RateLimitConstants.MaxRateLimitSurfaceArea)
        {
            throw new InvalidOperationException(
                $"Rate limit surface area {surfaceArea} ({channelCount} channel type(s) × " +
                $"{RateLimitConstants.PermitsConsumedPerChannel}) exceeds configured maximum " +
                $"{RateLimitConstants.MaxRateLimitSurfaceArea}. New channel added — re-evaluate shared vs " +
                "split per-tenant limiters, bump RateLimitConstants.MaxRateLimitSurfaceArea, and update " +
                "docs/adrs/011-shared-ingest-and-delivery-rate-limiting.md.");
        }

        Assert.Equal(RateLimitConstants.MaxRateLimitSurfaceArea, surfaceArea);
    }

    private static int CountChannelTypeMembers() =>
        typeof(ChannelTypes)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Count(f => f is { IsLiteral: true, FieldType: var t } && t == typeof(string));
}
