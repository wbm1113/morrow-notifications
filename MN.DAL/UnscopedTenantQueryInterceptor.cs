using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using MN.Interfaces;
using System.Data.Common;

namespace MN.DAL;

/// <summary>
/// Logs a warning whenever a SELECT is issued against a tenant-filtered table while the
/// tenant context is neither scoped to a tenant nor in admin mode.  The EF global query
/// filters will silently return empty results in this state — this interceptor makes that
/// invisible bug visible in the logs without touching any controller or repository.
/// </summary>
public class UnscopedTenantQueryInterceptor(
    ITenantContext tenantContext,
    ILogger<UnscopedTenantQueryInterceptor> logger) : DbCommandInterceptor
{
    // Matches AppDbContext DbSet property names that carry a HasQueryFilter.
    // EF Core (SQLite) wraps table names in double-quotes in generated SQL.
    private static readonly string[] FilteredTables = ["\"RoutingRules\"", "\"Messages\""];

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        WarnIfUnscoped(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        WarnIfUnscoped(command);
        return base.ReaderExecuting(command, eventData, result);
    }

    private void WarnIfUnscoped(DbCommand command)
    {
        if (tenantContext.IsAdminScope || tenantContext.CurrentTenantId.HasValue)
            return;

        foreach (var table in FilteredTables)
        {
            if (command.CommandText.Contains(table, StringComparison.Ordinal))
            {
                logger.LogWarning(
                    "Query against tenant-filtered table {Table} executed with no tenant context set. " +
                    "Results will be empty. Ensure ITenantContext.CurrentTenantId or IsAdminScope is " +
                    "configured before querying this table. If this is intentional, set IsAdminScope = true. " +
                    "SQL: {Sql}",
                    table.Trim('"'),
                    command.CommandText);

                return; // one warning per command is sufficient
            }
        }
    }
}
