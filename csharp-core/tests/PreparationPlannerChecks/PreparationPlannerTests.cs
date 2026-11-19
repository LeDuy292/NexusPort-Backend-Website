using NexusPort.Modules.Yard.Domain.Entities;
using NexusPort.Modules.Yard.Preparation.Application.Services;
using NexusPort.Modules.Yard.Preparation.Domain;
using Xunit;

namespace PreparationPlannerChecks;

public class PreparationPlannerTests
{
    private readonly PreparationPlanner _planner = new();
    private readonly YardBlock _blockA;
    private readonly YardBlock _blockB;
    private readonly YardBlock _stagingBlock;

    public PreparationPlannerTests()
    {
        _blockA = CreateBlock("A01");
        _blockB = CreateBlock("B01");
        _stagingBlock = CreateBlock("STAGING");
    }

    private YardBlock CreateBlock(string code, int maxCapacity = 100)
    {
        return new YardBlock(code, $"Khu bãi {code}")
        {
            MaxCapacity = maxCapacity,
            CurrentOccupancy = 40
        };
    }

    private YardSlot CreateSlot(YardBlock block, int bay, int row, int tier, string status = "empty", bool hasReeferPlug = false)
    {
        return new YardSlot(block.Id, bay, row, tier)
        {
            Status = status,
            HasReeferPlug = hasReeferPlug,
            Block = block
        };
    }

    [Fact]
    public void PlanTask_NoBlockingContainers_ReturnsZeroRehandle()
    {
        // Arrange
        var targetSlot = CreateSlot(_blockA, 1, 1, 3);
        var stack = new List<ContainerStackInfo>
        {
            new() { ContainerNo = "MSKU1234567", SlotId = targetSlot.Id, BlockId = _blockA.Id, Bay = 1, Row = 1, Tier = 3 }
        };

        var availableSlots = new List<YardSlot>();
        var activeReservations = new List<SlotReservation>();
        var config = new PreparationConfig { MaxRehandlePerTask = 6, StagingBlockId = null };
        var startBy = DateTimeOffset.UtcNow.AddMinutes(30);

        // Act
        var result = _planner.PlanTask("MSKU1234567", targetSlot, stack, availableSlots, activeReservations, config, startBy);

        // Assert
        Assert.True(result.IsSuccessful);
        Assert.False(result.NeedsApproval);
        Assert.Equal(0, result.RehandleCount);
        Assert.Empty(result.WorkOrders);
    }

    [Fact]
    public void PlanTask_NoBlockingContainers_WithStagingBlock_GeneratesStageWorkOrder()
    {
        // Arrange
        var targetSlot = CreateSlot(_blockA, 1, 1, 2);
        var stack = new List<ContainerStackInfo>
        {
            new() { ContainerNo = "MSKU1234567", SlotId = targetSlot.Id, BlockId = _blockA.Id, Bay = 1, Row = 1, Tier = 2 }
        };

        var stagingSlot = CreateSlot(_stagingBlock, 1, 1, 1);

        var availableSlots = new List<YardSlot> { stagingSlot };
        var activeReservations = new List<SlotReservation>();
        var config = new PreparationConfig { MaxRehandlePerTask = 6, StagingBlockId = _stagingBlock.Id };
        var startBy = DateTimeOffset.UtcNow.AddMinutes(30);

        // Act
        var result = _planner.PlanTask("MSKU1234567", targetSlot, stack, availableSlots, activeReservations, config, startBy);

        // Assert
        Assert.True(result.IsSuccessful);
        Assert.Single(result.WorkOrders);
        Assert.Equal(WorkOrderType.Stage, result.WorkOrders[0].Type);
        Assert.Equal(targetSlot.Id, result.WorkOrders[0].FromSlotId);
        Assert.Equal(stagingSlot.Id, result.WorkOrders[0].ToSlotId);
        Assert.Contains("khu chờ giao", result.WorkOrders[0].SelectionReason);
    }

    [Fact]
    public void PlanTask_OneBlockingContainer_SelectsBestCandidateSlot()
    {
        // Arrange: Container đích ở tầng 1, có 1 container chắn ở tầng 2
        var targetSlot = CreateSlot(_blockA, 1, 1, 1);
        var blockingSlot = CreateSlot(_blockA, 1, 1, 2);

        var stack = new List<ContainerStackInfo>
        {
            new() { ContainerNo = "TARG1111111", SlotId = targetSlot.Id, BlockId = _blockA.Id, Bay = 1, Row = 1, Tier = 1 },
            new() { ContainerNo = "BLOK2222222", SlotId = blockingSlot.Id, BlockId = _blockA.Id, Bay = 1, Row = 1, Tier = 2, IsReefer = false }
        };

        // Ô trống cùng khu (Bay 2, Row 1, Tier 1) và ô trống khác khu
        var candidateSlotSameBlock = CreateSlot(_blockA, 2, 1, 1);
        var candidateSlotOtherBlock = CreateSlot(_blockB, 5, 5, 1);

        var availableSlots = new List<YardSlot> { candidateSlotSameBlock, candidateSlotOtherBlock };
        var activeReservations = new List<SlotReservation>();
        var config = new PreparationConfig { MaxRehandlePerTask = 6, EstimatedMinutesPerMove = 8 };
        var startBy = DateTimeOffset.UtcNow.AddMinutes(30);

        // Act
        var result = _planner.PlanTask("TARG1111111", targetSlot, stack, availableSlots, activeReservations, config, startBy);

        // Assert
        Assert.True(result.IsSuccessful);
        Assert.Equal(1, result.RehandleCount);
        Assert.Single(result.WorkOrders);

        var move = result.WorkOrders[0];
        Assert.Equal(WorkOrderType.Rehandle, move.Type);
        Assert.Equal(1, move.Sequence);
        Assert.Equal("BLOK2222222", move.ContainerNo);
        Assert.Equal(blockingSlot.Id, move.FromSlotId);
        // Ưu tiên chọn ô cùng khu (Block A)
        Assert.Equal(candidateSlotSameBlock.Id, move.ToSlotId);
        Assert.Contains("Cùng khu", move.SelectionReason);
    }

    [Fact]
    public void PlanTask_MultipleBlockingContainers_OrderedFromTopToBottom()
    {
        // Arrange: Container đích ở tầng 1. Có 3 container chắn ở tầng 2, 3, 4
        var targetSlot = CreateSlot(_blockA, 1, 1, 1);
        var slotTier2 = CreateSlot(_blockA, 1, 1, 2);
        var slotTier3 = CreateSlot(_blockA, 1, 1, 3);
        var slotTier4 = CreateSlot(_blockA, 1, 1, 4);

        var stack = new List<ContainerStackInfo>
        {
            new() { ContainerNo = "TARGET100", SlotId = targetSlot.Id, BlockId = _blockA.Id, Bay = 1, Row = 1, Tier = 1 },
            new() { ContainerNo = "CONT_T2", SlotId = slotTier2.Id, BlockId = _blockA.Id, Bay = 1, Row = 1, Tier = 2 },
            new() { ContainerNo = "CONT_T3", SlotId = slotTier3.Id, BlockId = _blockA.Id, Bay = 1, Row = 1, Tier = 3 },
            new() { ContainerNo = "CONT_T4", SlotId = slotTier4.Id, BlockId = _blockA.Id, Bay = 1, Row = 1, Tier = 4 }
        };

        var cand1 = CreateSlot(_blockA, 2, 1, 1);
        var cand2 = CreateSlot(_blockA, 2, 2, 1);
        var cand3 = CreateSlot(_blockA, 2, 3, 1);

        var availableSlots = new List<YardSlot> { cand1, cand2, cand3 };
        var activeReservations = new List<SlotReservation>();
        var config = new PreparationConfig { MaxRehandlePerTask = 6 };
        var startBy = DateTimeOffset.UtcNow.AddMinutes(30);

        // Act
        var result = _planner.PlanTask("TARGET100", targetSlot, stack, availableSlots, activeReservations, config, startBy);

        // Assert
        Assert.True(result.IsSuccessful);
        Assert.Equal(3, result.RehandleCount);
        Assert.Equal(3, result.WorkOrders.Count);

        // Thứ tự đảo chuyển phải từ tầng cao nhất xuống thấp nhất: CONT_T4 -> CONT_T3 -> CONT_T2
        Assert.Equal("CONT_T4", result.WorkOrders[0].ContainerNo);
        Assert.Equal(1, result.WorkOrders[0].Sequence);

        Assert.Equal("CONT_T3", result.WorkOrders[1].ContainerNo);
        Assert.Equal(2, result.WorkOrders[1].Sequence);

        Assert.Equal("CONT_T2", result.WorkOrders[2].ContainerNo);
        Assert.Equal(3, result.WorkOrders[2].Sequence);
    }

    [Fact]
    public void PlanTask_NoValidSlotsAvailable_ReturnsFailureWithClearReason()
    {
        // Arrange
        var targetSlot = CreateSlot(_blockA, 1, 1, 1);
        var blockingSlot = CreateSlot(_blockA, 1, 1, 2);

        var stack = new List<ContainerStackInfo>
        {
            new() { ContainerNo = "TARG", SlotId = targetSlot.Id, BlockId = _blockA.Id, Bay = 1, Row = 1, Tier = 1 },
            new() { ContainerNo = "BLOK", SlotId = blockingSlot.Id, BlockId = _blockA.Id, Bay = 1, Row = 1, Tier = 2 }
        };

        var availableSlots = new List<YardSlot>(); // Không còn ô trống nào
        var activeReservations = new List<SlotReservation>();
        var config = new PreparationConfig { MaxRehandlePerTask = 6 };
        var startBy = DateTimeOffset.UtcNow.AddMinutes(30);

        // Act
        var result = _planner.PlanTask("TARG", targetSlot, stack, availableSlots, activeReservations, config, startBy);

        // Assert
        Assert.False(result.IsSuccessful);
        Assert.Contains("Không tìm thấy ô bãi trống hợp lệ", result.Reason);
    }

    [Fact]
    public void PlanTask_ReeferContainer_MustSelectSlotWithReeferPlug()
    {
        // Arrange: Container chắn là container lạnh
        var targetSlot = CreateSlot(_blockA, 1, 1, 1);
        var blockingSlot = CreateSlot(_blockA, 1, 1, 2);

        var stack = new List<ContainerStackInfo>
        {
            new() { ContainerNo = "TARG", SlotId = targetSlot.Id, BlockId = _blockA.Id, Bay = 1, Row = 1, Tier = 1 },
            new() { ContainerNo = "REEF_CONT", SlotId = blockingSlot.Id, BlockId = _blockA.Id, Bay = 1, Row = 1, Tier = 2, IsReefer = true }
        };

        var drySlot = CreateSlot(_blockA, 2, 1, 1, hasReeferPlug: false);
        var plugSlot = CreateSlot(_blockA, 3, 1, 1, hasReeferPlug: true);

        var availableSlots = new List<YardSlot> { drySlot, plugSlot };
        var activeReservations = new List<SlotReservation>();
        var config = new PreparationConfig { MaxRehandlePerTask = 6 };
        var startBy = DateTimeOffset.UtcNow.AddMinutes(30);

        // Act
        var result = _planner.PlanTask("TARG", targetSlot, stack, availableSlots, activeReservations, config, startBy);

        // Assert
        Assert.True(result.IsSuccessful);
        Assert.Single(result.WorkOrders);
        Assert.Equal(plugSlot.Id, result.WorkOrders[0].ToSlotId); // Bắt buộc vào ô có ổ điện
    }

    [Fact]
    public void PlanTask_DryContainer_MustNotSelectSlotWithReeferPlug()
    {
        // Arrange: Container chắn là container khô thường
        var targetSlot = CreateSlot(_blockA, 1, 1, 1);
        var blockingSlot = CreateSlot(_blockA, 1, 1, 2);

        var stack = new List<ContainerStackInfo>
        {
            new() { ContainerNo = "TARG", SlotId = targetSlot.Id, BlockId = _blockA.Id, Bay = 1, Row = 1, Tier = 1 },
            new() { ContainerNo = "DRY_CONT", SlotId = blockingSlot.Id, BlockId = _blockA.Id, Bay = 1, Row = 1, Tier = 2, IsReefer = false }
        };

        var drySlot = CreateSlot(_blockA, 4, 1, 1, hasReeferPlug: false);
        var plugSlot = CreateSlot(_blockA, 2, 1, 1, hasReeferPlug: true); // Gần hơn nhưng là ô reefer

        var availableSlots = new List<YardSlot> { drySlot, plugSlot };
        var activeReservations = new List<SlotReservation>();
        var config = new PreparationConfig { MaxRehandlePerTask = 6 };
        var startBy = DateTimeOffset.UtcNow.AddMinutes(30);

        // Act
        var result = _planner.PlanTask("TARG", targetSlot, stack, availableSlots, activeReservations, config, startBy);

        // Assert
        Assert.True(result.IsSuccessful);
        Assert.Single(result.WorkOrders);
        Assert.Equal(drySlot.Id, result.WorkOrders[0].ToSlotId); // Phải chọn ô khô, không được chiếm ô có điện
    }

    [Fact]
    public void PlanTask_AvoidsTargetContainerStack()
    {
        // Arrange: Ô trống nằm cùng stack với container đích (cùng Block, Bay, Row)
        var targetSlot = CreateSlot(_blockA, 1, 1, 1);
        var blockingSlot = CreateSlot(_blockA, 1, 1, 2);

        var stack = new List<ContainerStackInfo>
        {
            new() { ContainerNo = "TARG", SlotId = targetSlot.Id, BlockId = _blockA.Id, Bay = 1, Row = 1, Tier = 1 },
            new() { ContainerNo = "BLOK", SlotId = blockingSlot.Id, BlockId = _blockA.Id, Bay = 1, Row = 1, Tier = 2 }
        };

        var sameStackEmptySlot = CreateSlot(_blockA, 1, 1, 3); // Cùng stack
        var otherStackEmptySlot = CreateSlot(_blockA, 2, 1, 1); // Khác stack

        var availableSlots = new List<YardSlot> { sameStackEmptySlot, otherStackEmptySlot };
        var activeReservations = new List<SlotReservation>();
        var config = new PreparationConfig { MaxRehandlePerTask = 6 };
        var startBy = DateTimeOffset.UtcNow.AddMinutes(30);

        // Act
        var result = _planner.PlanTask("TARG", targetSlot, stack, availableSlots, activeReservations, config, startBy);

        // Assert
        Assert.True(result.IsSuccessful);
        Assert.Single(result.WorkOrders);
        Assert.Equal(otherStackEmptySlot.Id, result.WorkOrders[0].ToSlotId);
        Assert.NotEqual(sameStackEmptySlot.Id, result.WorkOrders[0].ToSlotId);
    }

    [Fact]
    public void PlanTask_AvoidsSlotsReservedByOtherTasks()
    {
        // Arrange
        var targetSlot = CreateSlot(_blockA, 1, 1, 1);
        var blockingSlot = CreateSlot(_blockA, 1, 1, 2);

        var stack = new List<ContainerStackInfo>
        {
            new() { ContainerNo = "TARG", SlotId = targetSlot.Id, BlockId = _blockA.Id, Bay = 1, Row = 1, Tier = 1 },
            new() { ContainerNo = "BLOK", SlotId = blockingSlot.Id, BlockId = _blockA.Id, Bay = 1, Row = 1, Tier = 2 }
        };

        var slotReserved = CreateSlot(_blockA, 2, 1, 1);
        var slotFree = CreateSlot(_blockA, 3, 1, 1);

        var availableSlots = new List<YardSlot> { slotReserved, slotFree };
        var activeReservations = new List<SlotReservation>
        {
            new(slotReserved.Id, Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(2))
        };

        var config = new PreparationConfig { MaxRehandlePerTask = 6 };
        var startBy = DateTimeOffset.UtcNow.AddMinutes(30);

        // Act
        var result = _planner.PlanTask("TARG", targetSlot, stack, availableSlots, activeReservations, config, startBy);

        // Assert
        Assert.True(result.IsSuccessful);
        Assert.Equal(slotFree.Id, result.WorkOrders[0].ToSlotId);
    }

    [Fact]
    public void PlanTask_ExceedingMaxRehandlePerTask_SetsNeedsApprovalAndNoAutoPlan()
    {
        // Arrange: Có 7 container chắn, vượt quá giới hạn 6
        var targetSlot = CreateSlot(_blockA, 1, 1, 1);
        var stack = new List<ContainerStackInfo>
        {
            new() { ContainerNo = "TARG", SlotId = targetSlot.Id, BlockId = _blockA.Id, Bay = 1, Row = 1, Tier = 1 }
        };

        for (int i = 2; i <= 8; i++)
        {
            stack.Add(new ContainerStackInfo
            {
                ContainerNo = $"CONT_{i}",
                SlotId = Guid.NewGuid(),
                BlockId = _blockA.Id,
                Bay = 1,
                Row = 1,
                Tier = i
            });
        }

        var availableSlots = new List<YardSlot>();
        var activeReservations = new List<SlotReservation>();
        var config = new PreparationConfig { MaxRehandlePerTask = 6 };
        var startBy = DateTimeOffset.UtcNow.AddMinutes(30);

        // Act
        var result = _planner.PlanTask("TARG", targetSlot, stack, availableSlots, activeReservations, config, startBy);

        // Assert
        Assert.True(result.NeedsApproval);
        Assert.Equal(7, result.RehandleCount);
        Assert.Empty(result.WorkOrders); // Không tự lập lệnh làm hàng
        Assert.Contains("vượt mức cho phép (6)", result.Reason);
        Assert.Contains("Cần điều độ phê duyệt", result.Reason);
    }

    [Fact]
    public void PlanBatch_ProcessesTasksByUrgencyAndAvoidsConflict()
    {
        // Arrange: 2 lệnh chuẩn bị container, task1 hẹn sớm hơn task2
        var task1 = new PreparationTask(Guid.NewGuid(), "CONT_EARLY", DateTimeOffset.UtcNow.AddHours(2), DateTimeOffset.UtcNow.AddMinutes(30), 80);
        var task2 = new PreparationTask(Guid.NewGuid(), "CONT_LATER", DateTimeOffset.UtcNow.AddHours(5), DateTimeOffset.UtcNow.AddHours(3), 50);

        var slot1 = CreateSlot(_blockA, 1, 1, 1);
        var slot2 = CreateSlot(_blockA, 3, 1, 1);

        var targetSlots = new Dictionary<string, YardSlot>
        {
            { "CONT_EARLY", slot1 },
            { "CONT_LATER", slot2 }
        };

        var stackTask1 = new List<ContainerStackInfo>
        {
            new() { ContainerNo = "CONT_EARLY", SlotId = slot1.Id, BlockId = _blockA.Id, Bay = 1, Row = 1, Tier = 1 },
            new() { ContainerNo = "BLOCKER_1", SlotId = Guid.NewGuid(), BlockId = _blockA.Id, Bay = 1, Row = 1, Tier = 2 }
        };

        var stackTask2 = new List<ContainerStackInfo>
        {
            new() { ContainerNo = "CONT_LATER", SlotId = slot2.Id, BlockId = _blockA.Id, Bay = 3, Row = 1, Tier = 1 },
            new() { ContainerNo = "BLOCKER_2", SlotId = Guid.NewGuid(), BlockId = _blockA.Id, Bay = 3, Row = 1, Tier = 2 }
        };

        var stackContainersByTask = new Dictionary<string, List<ContainerStackInfo>>
        {
            { "CONT_EARLY", stackTask1 },
            { "CONT_LATER", stackTask2 }
        };

        var candA = CreateSlot(_blockA, 2, 1, 1);
        var candB = CreateSlot(_blockA, 4, 1, 1);

        var availableSlots = new List<YardSlot> { candA, candB };
        var activeReservations = new List<SlotReservation>();
        var config = new PreparationConfig { MaxRehandlePerTask = 6 };

        // Act
        var batchResult = _planner.PlanBatch(
            new List<PreparationTask> { task2, task1 }, // Gửi không theo thứ tự
            targetSlots,
            stackContainersByTask,
            availableSlots,
            activeReservations,
            config);

        // Assert
        Assert.Equal(2, batchResult.Count);
        // Lệnh hẹn sớm hơn (task1) được xử lý trước
        Assert.Equal("CONT_EARLY", batchResult[0].Task.ContainerNo);
        Assert.Equal("CONT_LATER", batchResult[1].Task.ContainerNo);

        Assert.True(batchResult[0].Result.IsSuccessful);
        Assert.True(batchResult[1].Result.IsSuccessful);

        // Hai lệnh không được chọn trùng ô đích
        var destination1 = batchResult[0].Result.WorkOrders[0].ToSlotId;
        var destination2 = batchResult[1].Result.WorkOrders[0].ToSlotId;
        Assert.NotEqual(destination1, destination2);
    }
}
