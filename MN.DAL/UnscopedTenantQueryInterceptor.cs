using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using MN.Interfaces;
using System.Data.Common;

namespace MN.DAL;

/// <summary>
/// Detects SELECTs against tenant-filtered tables when no tenant context is set.  The EF
/// global query filters silently return empty results in this state — in DEBUG builds this
/// interceptor throws to surface the bug immediately; in Release it logs a warning.
/// </summary>
public class UnscopedTenantQueryInterceptor(
    ITenantContext tenantContext,
    ILogger<UnscopedTenantQueryInterceptor> logger) : DbCommandInterceptor
{
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        WarnIfUnscoped(command, eventData.Context);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        WarnIfUnscoped(command, eventData.Context);
        return base.ReaderExecuting(command, eventData, result);
    }

    private void WarnIfUnscoped(DbCommand command, DbContext? context)
    {
        if (tenantContext.IsAdminScope || tenantContext.CurrentTenantId.HasValue)
            return;

        if (context is null)
        {
            logger.LogDebug(
                "Skipping unscoped tenant query check: DbContext unavailable for command. SQL: {Sql}",
                command.CommandText);
            return;
        }

        var sql = TenantFilteredEntityRegistry.StripSqlComments(command.CommandText);

        // with more time - i think there is a way to inspect the guts of the EF query
        // in a more robust way; would like to explore that instead.  SQL inspection
        // on every select has performance implications i'd rather avoid.

        foreach (var match in TenantFilteredEntityRegistry.GetMatches(context.Model))
        {
            if (!match.Pattern.IsMatch(sql))
                continue;

#if DEBUG
            throw new InvalidOperationException(
                $"Query against tenant-filtered table '{match.TableName}' executed with no tenant context set. " +
                "Results would be empty. Set ITenantContext.CurrentTenantId or IsAdminScope = true before " +
                $"querying this table. SQL: {command.CommandText}");
#else
            logger.LogWarning(
                "Query against tenant-filtered table {Table} executed with no tenant context set. " +
                "Results will be empty. Ensure ITenantContext.CurrentTenantId or IsAdminScope is " +
                "configured before querying this table. If this is intentional, set IsAdminScope = true. " +
                "SQL: {Sql}",
                match.TableName,
                command.CommandText);
#endif
        }
    }
}
