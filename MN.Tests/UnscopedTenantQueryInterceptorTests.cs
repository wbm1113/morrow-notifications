using Microsoft.EntityFrameworkCore;
using MN.DAL;
using MN.Interfaces;

namespace MN.Tests;

public class UnscopedTenantQueryInterceptorTests : IDisposable
{
    private readonly AppDbContext _db;

    public UnscopedTenantQueryInterceptorTests()
    {
        TenantFilteredEntityRegistry.ResetCacheForTesting();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;

        _db = new AppDbContext(options, new TenantContext());
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        TenantFilteredEntityRegistry.ResetCacheForTesting();
    }

    [Fact]
    public void GetMatches_DiscoversAllEntitiesWithQueryFilters()
    {
        var matches = TenantFilteredEntityRegistry.GetMatches(_db.Model);

        Assert.Equal(3, matches.Count);
        Assert.Contains(matches, m => m.TableName == "RoutingRules");
        Assert.Contains(matches, m => m.TableName == "Messages");
        Assert.Contains(matches, m => m.TableName == "Dispatches");
    }

    [Theory]
    [InlineData(@"SELECT ""r"".""Id"" FROM ""RoutingRules"" AS ""r"" WHERE ""r"".""TenantId"" = @p0")]
    [InlineData(@"SELECT * FROM [RoutingRules] AS r WHERE r.TenantId = @p0")]
    [InlineData(@"SELECT * FROM RoutingRules WHERE TenantId = @p0")]
    public void IdentifierPattern_MatchesTableReference_WithAliasAndProviderQuoting(string sql)
    {
        var pattern = TenantFilteredEntityRegistry.BuildIdentifierPattern("RoutingRules");

        Assert.Matches(pattern, sql);
    }

    [Fact]
    public void IdentifierPattern_DoesNotMatchTableNameInsideComment()
    {
        var pattern = TenantFilteredEntityRegistry.BuildIdentifierPattern("RoutingRules");
        var sql = TenantFilteredEntityRegistry.StripSqlComments(
            @"SELECT 1 -- RoutingRules should not match in a line comment");

        Assert.DoesNotMatch(pattern, sql);
    }

    [Fact]
    public void IdentifierPattern_DoesNotMatchTableNameAsSubstringOfAnotherIdentifier()
    {
        var pattern = TenantFilteredEntityRegistry.BuildIdentifierPattern("Messages");

        Assert.DoesNotMatch(pattern, "SELECT * FROM NotificationMessages");
    }

    [Fact]
    public void GetMatches_DetectsUnscopedQueryAgainstFilteredTable()
    {
        var sql = TenantFilteredEntityRegistry.StripSqlComments(
            @"SELECT ""r"".""Id"" FROM ""RoutingRules"" AS ""r""");

        var matched = TenantFilteredEntityRegistry.GetMatches(_db.Model)
            .Any(m => m.Pattern.IsMatch(sql));

        Assert.True(matched);
    }
}
