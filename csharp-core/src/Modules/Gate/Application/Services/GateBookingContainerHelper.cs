using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexusPort.Infrastructure.Database;
using NexusPort.Modules.Booking.Domain.Entities;
using NexusPort.Modules.Gate.Application.DTOs;
using NexusPort.Modules.Gate.Domain.Validation;

namespace NexusPort.Modules.Gate.Application.Services;

/// <summary>
/// Helper dùng chung giữa GateVerificationService và GateContainerInspectionService
/// để truy vấn danh sách container dự kiến của Booking và chuẩn hóa metadata OCR container.onnx.
/// </summary>
internal static class GateBookingContainerHelper
{
    public static string NormalizeGateType(string? raw)
        => raw != null && raw.Contains("OUT", StringComparison.OrdinalIgnoreCase) ? "GateOut" : "GateIn";

    public static async Task<IReadOnlyList<ExpectedBookingContainerInfoDto>> LoadExpectedBookingContainersAsync(
        AppDbContext context,
        Guid bookingId,
        string gateType,
        CancellationToken cancellationToken)
    {
        var operations = await context.Set<BookingOperation>()
            .AsNoTracking()
            .Where(op => op.BookingId == bookingId)
            .ToListAsync(cancellationToken);

        var directionOps = operations
            .Where(op => string.IsNullOrWhiteSpace(op.ExpectedGateDirection) ||
                         string.Equals(op.ExpectedGateDirection, gateType, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var relevantOps = directionOps.Count > 0 ? directionOps : operations;

        var bookingContainers = await context.Set<BookingContainer>()
            .AsNoTracking()
            .Where(bc => bc.BookingId == bookingId)
            .ToListAsync(cancellationToken);

        var containerIds = relevantOps
            .Where(op => op.ContainerId.HasValue)
            .Select(op => op.ContainerId!.Value)
            .Concat(bookingContainers.Select(bc => bc.ContainerId))
            .Distinct()
            .ToList();

        var containerById = containerIds.Count > 0
            ? await context.Set<Container.Domain.Entities.Container>()
                .AsNoTracking()
                .Where(c => containerIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, cancellationToken)
            : new Dictionary<Guid, Container.Domain.Entities.Container>();

        var result = new List<ExpectedBookingContainerInfoDto>();
        var seenNumbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var op in relevantOps)
        {
            Container.Domain.Entities.Container? matchedEntity = null;
            if (op.ContainerId.HasValue)
            {
                containerById.TryGetValue(op.ContainerId.Value, out matchedEntity);
            }

            var number = Iso6346Validator.Normalize(op.ContainerNumber ?? matchedEntity?.ContainerNumber);
            if (!string.IsNullOrEmpty(number))
            {
                seenNumbers.Add(number);
            }

            result.Add(new ExpectedBookingContainerInfoDto
            {
                ContainerId = op.ContainerId ?? matchedEntity?.Id,
                BookingOperationId = op.Id,
                ContainerNumber = number,
                IsoSize = op.ContainerIsoSize,
                DbStatus = matchedEntity?.Status,
                SealNumber = op.SealNumber ?? matchedEntity?.SealNumber,
                OperationType = op.OperationType,
                ExpectedGateDirection = op.ExpectedGateDirection,
                VerificationStatus = op.VerificationStatus
            });
        }

        foreach (var bc in bookingContainers)
        {
            if (!containerById.TryGetValue(bc.ContainerId, out var c))
            {
                continue;
            }

            var number = Iso6346Validator.Normalize(c.ContainerNumber);
            if (!string.IsNullOrEmpty(number) && !seenNumbers.Add(number))
            {
                continue;
            }

            result.Add(new ExpectedBookingContainerInfoDto
            {
                ContainerId = c.Id,
                ContainerNumber = number,
                DbStatus = c.Status,
                SealNumber = c.SealNumber
            });
        }

        return result;
    }

    public static (IReadOnlyList<string> ContainerNumbers, IReadOnlyList<string> IsoSizes) ExtractExpectations(
        IReadOnlyList<ExpectedBookingContainerInfoDto> expectedContainers)
    {
        var expectedNumbers = expectedContainers
            .Select(x => Iso6346Validator.Normalize(x.ContainerNumber))
            .Where(x => !string.IsNullOrEmpty(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var expectedIsoSizes = expectedContainers
            .Select(x => x.IsoSize)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return (expectedNumbers, expectedIsoSizes);
    }

    public static string? EnrichOcrRawData(
        string? existingOcrRawData,
        string normalizedContainerNo,
        string? detectedType,
        double? confidence,
        bool? iso6346Valid,
        bool fromContainerOnnx)
    {
        if (string.IsNullOrEmpty(normalizedContainerNo) && !fromContainerOnnx && string.IsNullOrWhiteSpace(detectedType))
        {
            return existingOcrRawData;
        }

        if (!string.IsNullOrWhiteSpace(existingOcrRawData) &&
            existingOcrRawData.Contains("\"containerNumber\"", StringComparison.Ordinal))
        {
            return existingOcrRawData;
        }

        return JsonSerializer.Serialize(new
        {
            raw = existingOcrRawData,
            containerNumber = string.IsNullOrEmpty(normalizedContainerNo) ? null : normalizedContainerNo,
            containerType = detectedType,
            containerConfidence = confidence,
            iso6346Valid,
            fromContainerOnnx
        });
    }
}

