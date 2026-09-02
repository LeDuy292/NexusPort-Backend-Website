namespace NexusPort.Modules.Driver.Application.DTOs;

public class DriverDto
{
    public Guid Id { get; set; }
    public Guid CarrierId { get; set; }
    public string FullName { get; set; } = null!;
    public string Phone { get; set; } = null!;
    public string IdCardNumber { get; set; } = null!;
    public string LicenseNumber { get; set; } = null!;
    public string Status { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
}

public class CreateDriverDto
{
    public string FullName { get; set; } = null!;
    public string Phone { get; set; } = null!;
    public string IdCardNumber { get; set; } = null!;
    public string LicenseNumber { get; set; } = null!;
}

public class UpdateDriverDto
{
    public string FullName { get; set; } = null!;
    public string Phone { get; set; } = null!;
    public string IdCardNumber { get; set; } = null!;
}

public class DriverFilterDto
{
    public Guid? CarrierId { get; set; }
    public string? Status { get; set; }
    public string? SearchTerm { get; set; }
}
