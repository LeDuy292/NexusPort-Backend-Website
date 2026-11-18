using System;
using System.Collections.Generic;

namespace NexusPort.Modules.Yard.Application.DTOs;

public class PreAllocateDischargePlanRequestDto
{
    public Guid ManifestId { get; set; }
    public string? BerthCode { get; set; } = "B-01";
}

public class DischargeContainerItemDto
{
    public string ContainerNumber { get; set; } = string.Empty;
    public string? IsoType { get; set; }
    public bool IsReefer { get; set; }
    public decimal GrossWeightKg { get; set; }
    public string? BlNumber { get; set; }
    public string? Consignee { get; set; }
    public string? Commodity { get; set; }
    public string? SealNumber { get; set; }
    public string? ReservedSlot { get; set; }
    public Guid? ReservedSlotId { get; set; }
    public bool IsPreAllocated { get; set; } = false;
    public int AllocationScore { get; set; }
    public string? AllocationReason { get; set; }
    public string DischargeStatus { get; set; } = "PLANNED"; // PLANNED, DISCHARGED, IN_YARD
    public DateTime? DischargedAt { get; set; }
    public string? Condition { get; set; } = "GOOD";
    public string? InspectionNotes { get; set; }
    public List<string> DamagePhotos { get; set; } = new();
}

public class VesselDischargePlanDto
{
    public Guid ManifestId { get; set; }
    public string ManifestNumber { get; set; } = string.Empty;
    public string VesselName { get; set; } = string.Empty;
    public string VoyageNumber { get; set; } = string.Empty;
    public string BerthCode { get; set; } = "B-01";
    public string Direction { get; set; } = "IMPORT";
    public string Status { get; set; } = "PORT_APPROVED";
    public DateTime? Eta { get; set; }
    public int TotalContainers { get; set; }
    public decimal TotalWeightKg { get; set; }
    public int ReeferCount { get; set; }
    public int TwentyFtCount { get; set; }
    public int FortyFtCount { get; set; }
    public int AllocatedCount { get; set; }
    public int DischargedCount { get; set; }
    public int InYardCount { get; set; }
    public bool IsPreAllocated { get; set; } = false;
    public DateTime? PreAllocatedAt { get; set; }
    public List<DischargeContainerItemDto> Containers { get; set; } = new();
}

public class TallyScanRequestDto
{
    public string ContainerNumber { get; set; } = string.Empty;
    public string? Condition { get; set; } = "GOOD"; // GOOD, DAMAGED, SEAL_BROKEN
    public string? Notes { get; set; }
    public string? ActualSlot { get; set; }
    public bool ConfirmPutdown { get; set; } = false;
    public List<string>? DamagePhotos { get; set; } = new();
}

public class TallyScanResultDto
{
    public bool IsMatch { get; set; }
    public bool IsException { get; set; }
    public string ContainerNumber { get; set; } = string.Empty;
    public Guid? ManifestId { get; set; }
    public string? VesselName { get; set; }
    public string? VoyageNumber { get; set; }
    public string? BlNumber { get; set; }
    public string? Consignee { get; set; }
    public string? ReservedSlot { get; set; }
    public int AllocationScore { get; set; }
    public string? AllocationReason { get; set; }
    public string DischargeStatus { get; set; } = "DISCHARGED";
    public DateTime? DischargedAt { get; set; }
    public string? Condition { get; set; }
    public bool IsReefer { get; set; }
    public List<string> DamagePhotos { get; set; } = new();
    public string? AssignedBufferSlot { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? SuggestedAction { get; set; }
}

public class OverrideSlotRequestDto
{
    public Guid ManifestId { get; set; }
    public string ContainerNumber { get; set; } = string.Empty;
    public string NewSlotCoordinate { get; set; } = string.Empty;
    public string? Reason { get; set; }
}
