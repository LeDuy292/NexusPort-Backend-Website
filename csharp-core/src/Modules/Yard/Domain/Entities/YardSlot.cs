using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Yard.Domain.Entities;

public class YardSlot : BaseEntity
{
    public Guid YardBlockId { get; set; }
    
    // Slot coordinates within the block
    public int Bay { get; set; }
    public int Row { get; set; }
    public int Tier { get; set; }

    // Status: "empty", "reserved", "occupied", "maintenance" (from enum)
    public string Status { get; set; } = "empty";

    public decimal? MaxWeightKg { get; set; }
    public bool HasReeferPlug { get; set; }

    // Navigation property
    public YardBlock Block { get; set; } = null!;

    public YardSlot() { }

    public YardSlot(Guid yardBlockId, int bay, int row, int tier)
    {
        YardBlockId = yardBlockId;
        Bay = bay;
        Row = row;
        Tier = tier;
        Status = "empty";
    }

    public bool IsAvailable()
    {
        return Status == "empty";
    }

    public void MarkOccupied()
    {
        Status = "occupied";
    }

    public void MarkEmpty()
    {
        Status = "empty";
    }

    public void ToggleMaintenance()
    {
        if (Status == "occupied")
            throw new InvalidOperationException("Cannot maintain an occupied slot.");
            
        Status = Status == "maintenance" ? "empty" : "maintenance";
    }
}
