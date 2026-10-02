using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NexusPort.Modules.Carrier.Application.DTOs;

namespace NexusPort.Modules.Carrier.Application.Services;

public interface ICarrierService
{
    Task<List<CompanyDto>> GetAllAsync(string? search);
    Task<CompanyDto?> GetByIdAsync(Guid id);
    Task<CompanyDto> CreateAsync(CreateCompanyDto dto);
    Task<CompanyDto?> UpdateAsync(Guid id, UpdateCompanyDto dto);
    Task<bool> ChangeStatusAsync(Guid id, string status);
}
