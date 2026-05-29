using MN.Interfaces;

namespace MN.DAL;

public class TenantContext : ITenantContext
{
    public Guid? CurrentTenantId { get; set; }
    public bool IsAdminScope { get; set; } = false;
}
