using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace MN.Tests;

/// <summary>
/// Validates the DI container's service graph at build time.
///
/// The core risk: a singleton that captures a scoped ITenantContext shares one tenant
/// identity for the lifetime of the process — every request would read/write as that
/// tenant, a total isolation failure. ASP.NET Core's scope validation catches this
/// class of mistake if you ask it to.
///
/// These tests re-build the service collection with ValidateScopes + ValidateOnBuild
/// both enabled. If any singleton takes a scoped dependency (directly or transitively)
/// the provider throws InvalidOperationException at Build() time, not at runtime.
/// </summary>
public class DependencyInjectionTests
{
    /// <summary>
    /// Building the service provider with strict scope validation must not throw.
    /// Any singleton that directly or transitively depends on ITenantContext (scoped)
    /// will fail here before it can cause a tenant isolation bug in production.
    /// </summary>
    [Fact]
    public void ServiceProvider_WithScopeValidation_BuildsWithoutError()
    {
        IServiceCollection? captured = null;

        using var factory = new CapturingWebApplicationFactory(services =>
        {
            captured = services;
        });

        // Force the host to build so ConfigureServices callbacks run.
        _ = factory.Services;

        Assert.NotNull(captured);

        var exception = Record.Exception(() =>
            captured.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true
            }));

        Assert.Null(exception);
    }

    /// <summary>
    /// Explicitly asserts that every singleton service in the container does NOT have
    /// ITenantContext as a direct constructor dependency. This is a belt-and-suspenders
    /// check: ValidateOnBuild catches transitive scope violations; this catches the
    /// most direct and readable version of the mistake and produces a clearer failure
    /// message that names the offending type.
    /// </summary>
    [Fact]
    public void Singleton_Services_DoNotDirectlyDependOn_ITenantContext()
    {
        IServiceCollection? captured = null;

        using var factory = new CapturingWebApplicationFactory(services =>
        {
            captured = services;
        });

        _ = factory.Services;

        Assert.NotNull(captured);

        var singletonImplementationTypes = captured
            .Where(d => d.Lifetime == ServiceLifetime.Singleton)
            .Select(d => d.ImplementationType)
            .Where(t => t is not null)
            .ToList();

        var tenantContextType = typeof(MN.Interfaces.ITenantContext);

        var offenders = singletonImplementationTypes
            .Where(implType => implType!
                .GetConstructors()
                .Any(ctor => ctor.GetParameters()
                    .Any(p => tenantContextType.IsAssignableFrom(p.ParameterType))))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"The following singleton(s) directly inject ITenantContext (scoped), " +
            $"which would cause all requests to share the same tenant identity:\n" +
            string.Join("\n", offenders.Select(t => $"  - {t!.FullName}")));
    }

    // Minimal factory subclass that captures the IServiceCollection before the host
    // builds it. We deliberately don't extend CustomWebApplicationFactory so this test
    // doesn't inherit its SQLite file setup/teardown overhead.
    private sealed class CapturingWebApplicationFactory(Action<IServiceCollection> onConfigure)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(onConfigure);

            // Point at an in-memory DB so no file is created on disk.
            builder.UseSetting(
                "ConnectionStrings:DefaultConnection",
                $"Data Source=di-test-{Guid.NewGuid():N}.db;Mode=Memory;Cache=Shared");
        }
    }
}
