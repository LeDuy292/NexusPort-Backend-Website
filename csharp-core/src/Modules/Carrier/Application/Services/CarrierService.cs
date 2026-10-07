using NexusPort.Shared.Exceptions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using NexusPort.Modules.Carrier.Application.DTOs;
using NexusPort.Modules.Carrier.Domain.Entities;
using NexusPort.Modules.Carrier.Infrastructure.Persistence;

namespace NexusPort.Modules.Carrier.Application.Services;

public class CarrierService : ICarrierService
{
    private readonly CarrierDbContext _context;

    public CarrierService(CarrierDbContext context)
    {
        _context = context;
    }

    public async Task<List<CompanyDto>> GetAllAsync(string? search)
    {
        var query = _context.Carriers.AsQueryable();
        if (!string.IsNullOrEmpty(search))
        {
            query = query.Where(c => c.CompanyName.Contains(search) || c.TaxCode.Contains(search));
        }

        return await query.Select(c => new CompanyDto
        {
            Id = c.Id,
            CompanyName = c.CompanyName,
            TaxCode = c.TaxCode,
            Address = c.Address,
            Phone = c.Phone,
            Email = c.Email,
            ContactPerson = c.ContactPerson,
            Status = c.Status.ToString()
        }).ToListAsync();
    }

    public async Task<CompanyDto?> GetByIdAsync(Guid id)
    {
        var c = await _context.Carriers.FindAsync(id);
        if (c == null) return null;
        return new CompanyDto
        {
            Id = c.Id,
            CompanyName = c.CompanyName,
            TaxCode = c.TaxCode,
            Address = c.Address,
            Phone = c.Phone,
            Email = c.Email,
            ContactPerson = c.ContactPerson,
            Status = c.Status.ToString()
        };
    }

    public async Task<CompanyDto> CreateAsync(CreateCompanyDto dto)
    {
        if (await _context.Carriers.AnyAsync(c => c.TaxCode == dto.TaxCode))
        {
            throw new ValidationException("TaxCode", "Mã số thuế / Mã hãng tàu này đã tồn tại trong hệ thống.");
        }
        
        if (!string.IsNullOrWhiteSpace(dto.Email))
        {
            if (await _context.Carriers.AnyAsync(c => c.Email == dto.Email))
            {
                throw new ValidationException("Email", "Email này đã được sử dụng cho một hãng tàu khác.");
            }
            
            var emailExists = await _context.Database.SqlQueryRaw<bool>("SELECT EXISTS(SELECT 1 FROM users WHERE email = {0}) AS \"Value\"", dto.Email.ToLower()).SingleOrDefaultAsync();
            if (emailExists)
            {
                throw new ValidationException("Email", "Email này đã được đăng ký tài khoản trong hệ thống.");
            }
        }

        using var transaction = await _context.Database.BeginTransactionAsync();
        try
        {
            var entity = new CarrierEntity(dto.CompanyName, dto.TaxCode, dto.Address, dto.Phone, dto.Email, dto.ContactPerson);
            _context.Carriers.Add(entity);
            await _context.SaveChangesAsync();

            if (!string.IsNullOrWhiteSpace(dto.Email))
            {
                var userId = Guid.NewGuid();

                await _context.Database.ExecuteSqlRawAsync(
                    "INSERT INTO users (id, email, username, password, full_name, role, is_active, created_at, updated_at) VALUES ({0}, {1}, {2}, crypt('123456', gen_salt('bf')), {3}, 'Transport Company', true, NOW(), NOW())",
                    userId, dto.Email.ToLower(), dto.Email.ToLower(), dto.CompanyName);

                await _context.Database.ExecuteSqlRawAsync(
                    "INSERT INTO carrier_users (carrier_id, user_id) VALUES ({0}, {1})",
                    entity.Id, userId);
            }

            await transaction.CommitAsync();
            return await GetByIdAsync(entity.Id) ?? throw new Exception("Created entity not found.");
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task<CompanyDto?> UpdateAsync(Guid id, UpdateCompanyDto dto)
    {
        var entity = await _context.Carriers.FindAsync(id);
        if (entity == null) return null;

        entity.CompanyName = dto.CompanyName;
        entity.TaxCode = dto.TaxCode;
        entity.Address = dto.Address;
        entity.Phone = dto.Phone;
        entity.Email = dto.Email;
        entity.ContactPerson = dto.ContactPerson;

        await _context.SaveChangesAsync();
        return await GetByIdAsync(id);
    }

    public async Task<bool> ChangeStatusAsync(Guid id, string status)
    {
        var entity = await _context.Carriers.FindAsync(id);
        if (entity == null) return false;

        if (Enum.TryParse<NexusPort.Modules.Carrier.Domain.Enums.CompanyStatus>(status, true, out var parsedStatus))
        {
            entity.Status = parsedStatus;
            await _context.SaveChangesAsync();
            return true;
        }
        return false;
    }
}
