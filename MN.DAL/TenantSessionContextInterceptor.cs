using Microsoft.EntityFrameworkCore.Diagnostics;
using MN.Interfaces;
using System.Data.Common;

namespace MN.DAL;

/// <summary>
/// No-op locally (SQLite has no SESSION_CONTEXT support).
///
/// In production against Azure SQL with Row-Level Security, replace the no-op bodies with
/// an invocation of sp_set_session_context:
///
///   await command.Connection!.ExecuteAsync(
///       "EXEC sp_set_session_context @key = N'TenantId', @value = @tid",
///       new { tid = tenantContext.CurrentTenantId });
///
/// This bakes tenant isolation into the database engine itself, providing defense-in-depth
/// independent of any application-layer filtering.
/// </summary>
public class TenantSessionContextInterceptor : DbCommandInterceptor
{
    public TenantSessionContextInterceptor(ITenantContext tenantContext)
    {
        // Retained for documentation and future production wiring; no-op on SQLite.
        _ = tenantContext;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken ct = default) =>
        ValueTask.FromResult(result);
}
