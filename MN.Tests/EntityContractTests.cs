using System.Reflection;
using MN.Entities;

namespace MN.Tests;

public class EntityContractTests
{
    [Fact]
    public void AllEntitiesWithTenantId_MustImplementITenantScoped()
    {
        var entityTypes = typeof(RoutingRule).Assembly
            .GetExportedTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .Where(t => t.GetProperty("TenantId", BindingFlags.Public | BindingFlags.Instance) is not null)
            .ToList();

        var violations = entityTypes
            .Where(t => !typeof(ITenantScoped).IsAssignableFrom(t))
            .Select(t => t.Name)
            .ToList();

        Assert.True(
            violations.Count == 0,
            $"The following types have a TenantId property but do not implement ITenantScoped: {string.Join(", ", violations)}");
    }
}
