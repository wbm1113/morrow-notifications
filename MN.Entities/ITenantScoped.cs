namespace MN.Entities;

public interface ITenantScoped
{
    Guid TenantId { get; set; }
}
