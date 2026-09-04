using System;
using System.ComponentModel.DataAnnotations;

namespace NexusPort.Modules.Carrier.Application.DTOs;

public class CompanyDto
{
    public Guid Id { get; set; }
    public string CompanyName { get; set; } = string.Empty;
    public string? TaxCode { get; set; }
    public string? Address { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? ContactPerson { get; set; }
    public string Status { get; set; } = "active";
}

public class CreateCompanyDto
{
    [Required(ErrorMessage = "Tên hãng tàu là bắt buộc")]
    public string CompanyName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Mã SCAC (TaxCode) là bắt buộc")]
    public string? TaxCode { get; set; }

    public string? Address { get; set; }

    [Phone(ErrorMessage = "Số điện thoại không hợp lệ")]
    public string? Phone { get; set; }

    [Required(ErrorMessage = "Email là bắt buộc")]
    [EmailAddress(ErrorMessage = "Email không hợp lệ")]
    public string? Email { get; set; }

    [Required(ErrorMessage = "Người liên hệ là bắt buộc")]
    public string? ContactPerson { get; set; }
}

public class UpdateCompanyDto : CreateCompanyDto
{
}

public class ChangeStatusDto
{
    [Required]
    public string Status { get; set; } = string.Empty;
}
