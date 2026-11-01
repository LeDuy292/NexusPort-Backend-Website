using NexusPort.Shared.Kernel;
using NexusPort.Modules.Carrier.Domain.Enums;

namespace NexusPort.Modules.Carrier.Domain.Entities;

public class CarrierEntity : BaseEntity, IAggregateRoot
{
    public string CompanyName { get; set; } = string.Empty;
    public string? TaxCode { get; set; }
    public string? Address { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? ContactPerson { get; set; }
    public CompanyStatus Status { get; set; } = CompanyStatus.inactive; // active, suspended, inactive

    public CarrierEntity() { }

    public CarrierEntity(string companyName, string? taxCode, string? address, string? phone, string? email, string? contactPerson)
    {
        CompanyName = companyName;
        TaxCode = taxCode;
        Address = address;
        Phone = phone;
        Email = email;
        ContactPerson = contactPerson;
    }
}
