using NexusPort.Modules.Yard.Application.DTOs;

namespace NexusPort.Modules.Yard.Application.Interfaces;

public interface IYardReceivingService
{
    Task<YardContainerVerificationResultDto> VerifyContainerAndSealAsync(VerifyYardContainerDto dto, CancellationToken cancellationToken = default);
    Task<YardReceiptDto> CreateReceiptAsync(CreateYardReceiptDto dto, string userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<YardReceiptDto>> GetReceiptsAsync(string? blockCode = null, CancellationToken cancellationToken = default);
    Task<YardReceiptDto?> GetReceiptByIdAsync(Guid id, CancellationToken cancellationToken = default);
}
