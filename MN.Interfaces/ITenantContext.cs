namespace MN.Interfaces;

public interface ITenantContext
{
    Guid? CurrentTenantId { get; set; }

    /// <summary>
    /// Explicit opt-in to bypass EF global query filters for cross-tenant admin operations.
    /// Default is false (fail-closed): queries on filtered tables return nothing unless
    /// CurrentTenantId is set or this flag is explicitly set to true.
    /// </summary>
    bool IsAdminScope { get; set; }
}
