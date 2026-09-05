using NexusPort.Modules.Driver.Application.DTOs;
using NexusPort.Modules.Driver.Application.Interfaces;

namespace NexusPort.Modules.Driver.Application.Services;

public class DriverService : IDriverService
{
    private readonly IDriverRepository _repository;

    public DriverService(IDriverRepository repository)
    {
        _repository = repository;
    }

    public async Task<IReadOnlyList<DriverDto>> GetAllAsync(DriverFilterDto filter, CancellationToken cancellationToken = default)
    {
        var entities = await _repository.GetAllAsync(filter, cancellationToken);
        return entities.Select(e => new DriverDto
        {
            Id = e.Id,
            CarrierId = e.CarrierId,
            FullName = e.FullName,
            Phone = e.Phone,
            IdCardNumber = e.IdCardNumber,
            LicenseNumber = e.LicenseNumber,
            Status = e.Status,
            CreatedAt = e.CreatedAt
        }).ToList();
    }

    public async Task<DriverDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _repository.GetByIdAsync(id, cancellationToken);
        if (entity == null) return null;
        return new DriverDto
        {
            Id = entity.Id,
            CarrierId = entity.CarrierId,
            FullName = entity.FullName,
            Phone = entity.Phone,
            IdCardNumber = entity.IdCardNumber,
            LicenseNumber = entity.LicenseNumber,
            Status = entity.Status,
            CreatedAt = entity.CreatedAt
        };
    }

    public async Task<DriverDto> CreateAsync(Guid carrierId, CreateDriverDto dto, CancellationToken cancellationToken = default)
    {
        if (await _repository.ExistsByLicenseAsync(carrierId, dto.LicenseNumber, null, cancellationToken))
        {
            throw new InvalidOperationException("Driver with this license number already exists for this company.");
        }

        var entity = new NexusPort.Modules.Driver.Domain.Entities.Driver(
            carrierId: carrierId,
            fullName: dto.FullName,
            licenseNumber: dto.LicenseNumber,
            phone: dto.Phone,
            idCardNumber: dto.IdCardNumber,
            status: "active"
        );

        await _repository.AddAsync(entity, cancellationToken);
        
        return new DriverDto
        {
            Id = entity.Id,
            CarrierId = entity.CarrierId,
            FullName = entity.FullName,
            Phone = entity.Phone,
            IdCardNumber = entity.IdCardNumber,
            LicenseNumber = entity.LicenseNumber,
            Status = entity.Status,
            CreatedAt = entity.CreatedAt
        };
    }

    public async Task<DriverDto> UpdateAsync(Guid id, UpdateDriverDto dto, CancellationToken cancellationToken = default)
    {
        var entity = await _repository.GetByIdAsync(id, cancellationToken);
        if (entity == null) throw new KeyNotFoundException("Driver not found.");

        entity.FullName = dto.FullName;
        entity.Phone = dto.Phone;
        entity.IdCardNumber = dto.IdCardNumber;

        await _repository.UpdateAsync(entity, cancellationToken);

        return new DriverDto
        {
            Id = entity.Id,
            CarrierId = entity.CarrierId,
            FullName = entity.FullName,
            Phone = entity.Phone,
            IdCardNumber = entity.IdCardNumber,
            LicenseNumber = entity.LicenseNumber,
            Status = entity.Status,
            CreatedAt = entity.CreatedAt
        };
    }

    public async Task ToggleStatusAsync(Guid id, string status, CancellationToken cancellationToken = default)
    {
        var entity = await _repository.GetByIdAsync(id, cancellationToken);
        if (entity == null) throw new KeyNotFoundException("Driver not found.");

        if (status == "active" || status == "inactive" || status == "banned")
        {
            entity.Status = status;
            await _repository.UpdateAsync(entity, cancellationToken);
        }
        else
        {
            throw new ArgumentException("Invalid driver status.");
        }
    }
}
