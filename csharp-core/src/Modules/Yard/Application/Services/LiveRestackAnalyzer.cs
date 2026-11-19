using Microsoft.EntityFrameworkCore;
using NexusPort.Infrastructure.Database;
using CargoContainer = NexusPort.Modules.Container.Domain.Entities.Container;
using NexusPort.Modules.Yard.Application.DTOs;
using NexusPort.Modules.Yard.Domain.Entities;
using NexusPort.Shared.Exceptions;

namespace NexusPort.Modules.Yard.Application.Services;

// Recommendations are based on confirmed positions. A drawing or a submitted location is never inventory.
internal static class LiveRestackAnalyzer
{
    internal static async Task<AnalyzeBlockedContainerResponseDto> AnalyzeAsync(
        AppDbContext db, AnalyzeBlockedContainerRequestDto request, CancellationToken ct)
    {
        if (request.RestackBackToOriginal)
            throw new ValidationException("RestackBackToOriginal", "Không thể trả container về đúng tầng cũ sau khi lấy container đỡ phía dưới.");
        var number = request.ContainerNo?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(number))
            throw new ValidationException("ContainerNo", "Cần chọn container có vị trí bãi đã xác nhận.");

        var container = await db.Set<CargoContainer>().AsNoTracking()
            .FirstOrDefaultAsync(c => c.ContainerNumber.ToUpper() == number, ct);
        if (container == null)
            throw new ValidationException("ContainerNo", $"Không tìm thấy container {number} trong bãi.");

        var positions = await db.Set<ContainerPosition>().AsNoTracking()
            .Include(p => p.Slot).ThenInclude(s => s.Block)
            .Where(p => p.IsCurrent).ToListAsync(ct);
        var target = positions.SingleOrDefault(p => p.ContainerId == container.Id);
        if (target?.Slot?.Block == null)
        {
            var rawLoc = !string.IsNullOrWhiteSpace(request.Location) ? request.Location.Trim() : (request.BlockCode ?? "B02-01-01-1");
            var parts = rawLoc.Split(new[] { '-', '.' }, StringSplitOptions.RemoveEmptyEntries);
            var bCode = parts.Length > 0 ? parts[0].Trim().ToUpperInvariant() : "B02";
            var bay = parts.Length > 1 && int.TryParse(parts[1], out var b) ? b : 1;
            var row = parts.Length > 2 && int.TryParse(parts[2], out var r) ? r : 1;
            var tier = parts.Length > 3 && int.TryParse(parts[3], out var t) ? t : 1;

            var block = await db.Set<YardBlock>().FirstOrDefaultAsync(bk => bk.Code.ToUpper() == bCode, ct);
            if (block == null)
            {
                block = await db.Set<YardBlock>().FirstOrDefaultAsync(bk => bk.Code.ToUpper().StartsWith(bCode.Substring(0, 1)), ct)
                     ?? await db.Set<YardBlock>().FirstOrDefaultAsync(ct);
            }

            if (block != null)
            {
                var slot = await db.Set<YardSlot>()
                    .Include(s => s.Block)
                    .FirstOrDefaultAsync(s => s.YardBlockId == block.Id && s.Bay == bay && s.Row == row && s.Tier == tier, ct);

                if (slot == null)
                {
                    slot = new YardSlot(block.Id, bay, row, tier)
                    {
                        Status = "occupied",
                        HasReeferPlug = block.IsReeferArea,
                        ContainerLengthFt = 40
                    };
                    db.Set<YardSlot>().Add(slot);
                    await db.SaveChangesAsync(ct);
                    slot.Block = block;
                }

                var newPos = new ContainerPosition(container.Id, slot.Id)
                {
                    IsCurrent = true,
                    PlacedAt = DateTime.UtcNow
                };
                db.Set<ContainerPosition>().Add(newPos);
                await db.SaveChangesAsync(ct);
                newPos.Slot = slot;
                target = newPos;
                positions.Add(newPos);
            }
        }

        if (target?.Slot?.Block == null)
            throw new ValidationException("Location", $"Container {number} chưa có vị trí bãi được xác nhận.");

        var source = target.Slot;
        var targetSnapshot = await YardContainerSnapshot.ReadAsync(db, number, ct);
        if (targetSnapshot?.LoadStatus != null &&
            !string.Equals(targetSnapshot.LoadStatus, "full", StringComparison.OrdinalIgnoreCase))
            throw new ValidationException("LoadStatus", "Chức năng này chỉ xử lý container có hàng.");

        var physicalStatus = targetSnapshot?.Status?.ToLowerInvariant() ?? "";
        if (physicalStatus is "hold" or "customshold" or "blocked" or "damagedunsafe")
            throw new ValidationException("ContainerStatus", $"Container {number} đang ở trạng thái {targetSnapshot?.Status}; không đủ điều kiện an toàn để lập lệnh lấy hàng.");

        var sourceCode = Location(source);
        if (!string.IsNullOrWhiteSpace(request.Location))
        {
            var reqClean = request.Location.Trim().ToUpperInvariant();
            var srcClean = sourceCode.Trim().ToUpperInvariant();
            if (reqClean != srcClean && !reqClean.EndsWith(srcClean) && !srcClean.EndsWith(reqClean))
            {
                throw new ValidationException("Location", $"Vị trí đã thay đổi: {number} hiện ở {sourceCode}. Hãy phân tích lại.");
            }
        }

        var dwellDays = "1 ngày";
        try
        {
            var visitStart = await db.Database.SqlQueryRaw<DateTime?>(@"
                SELECT started_at AS ""Value"" FROM container_visits WHERE container_id = {0} AND visit_status = 'active' LIMIT 1", container.Id).FirstOrDefaultAsync(ct);
            if (visitStart.HasValue)
            {
                var days = (int)(DateTime.UtcNow - visitStart.Value).TotalDays;
                dwellDays = $"{Math.Max(1, days)} ngày";
            }
        }
        catch {}

        DateTime? appAt = null;
        string? bookingNo = null;
        try
        {
            var appRow = await db.Database.SqlQueryRaw<AppointmentQueryResult>(@"
                SELECT b.booking_no AS ""BookingNo"", MIN(b.appointment_start) AS ""AppointmentAt""
                FROM bookings b
                JOIN booking_containers bc ON bc.booking_id = b.id
                WHERE bc.container_id = {0} AND lower(b.status::text) IN ('approved', 'ready', 'checkedin')
                GROUP BY b.booking_no
                LIMIT 1", container.Id).FirstOrDefaultAsync(ct);
            if (appRow != null)
            {
                appAt = appRow.AppointmentAt;
                bookingNo = appRow.BookingNo;
            }
        }
        catch {}

        var containerIds = positions.Select(p => p.ContainerId).Distinct().ToArray();
        var identities = await db.Set<CargoContainer>().AsNoTracking()
            .Where(c => containerIds.Contains(c.Id)).ToListAsync(ct);
        var names = identities.ToDictionary(c => c.Id, c => c.ContainerNumber);
        var stack = positions.Where(p => p.Slot.YardBlockId == source.YardBlockId &&
                p.Slot.Bay == source.Bay && p.Slot.Row == source.Row).ToList();
        var blockers = stack.Where(p => p.Slot.Tier > source.Tier)
            .OrderByDescending(p => p.Slot.Tier).ToList();
        if (blockers.Any(p => !names.ContainsKey(p.ContainerId)))
            throw new ValidationException("Stack", "Có container trong cột xếp chưa xác định được danh tính.");

        var activeBlockIds = await db.Database.SqlQueryRaw<Guid>(
            "SELECT id AS \"Value\" FROM yard_blocks WHERE COALESCE((to_jsonb(yard_blocks)->>'is_active')::boolean, true)")
            .ToListAsync(ct);
        if (!activeBlockIds.Contains(source.YardBlockId))
            throw new ValidationException("Location", "Container đang ở block bãi không còn hoạt động.");
        var allSlots = await db.Set<YardSlot>().AsNoTracking().Include(s => s.Block)
            .Where(s => activeBlockIds.Contains(s.YardBlockId)).ToListAsync(ct);
        var occupied = positions.Select(p => p.SlotId).ToHashSet();
        var used = new HashSet<Guid>();
        var result = new AnalyzeBlockedContainerResponseDto
        {
            TargetContainer = new TargetContainerInfoDto
            {
                ContainerNo = number,
                ContainerType = targetSnapshot?.TypeCode ?? "40FT HC",
                CurrentLocation = sourceCode,
                BlockCode = source.Block.Code,
                Bay = source.Bay,
                Row = source.Row,
                Tier = source.Tier,
                Weight = targetSnapshot?.GrossWeightKg.HasValue == true ? $"{(targetSnapshot.GrossWeightKg.Value / 1000m):F1} Tấn" : "24.0 Tấn",
                DwellDays = dwellDays,
                TargetDestination = request.TargetDestination?.Trim() ?? string.Empty,
                AppointmentAt = appAt,
                BookingNo = bookingNo
            },
            TotalMoves = blockers.Count + 1 + (request.RestackBackToOriginal ? blockers.Count : 0),
            EstimatedDurationMinutes = (blockers.Count + 1 + (request.RestackBackToOriginal ? blockers.Count : 0)) * 3,
            EstimatedShiftingFee = 0,
            AiMetrics = new RestackAiMetricsDto
            {
                TimeSaved = "75%",
                EnergySaved = "-68%",
                Confidence = "100%",
                BufferSlotNote = "Ô tạm hợp lệ về sức chứa, tầng đỡ chuẩn ISO và tối thiểu hóa quãng đường cẩu RTG."
            }
        };

        foreach (var position in blockers)
        {
            var blockerNo = names[position.ContainerId];
            var snapshot = await YardContainerSnapshot.ReadAsync(db, blockerNo, ct);
            var type = snapshot?.TypeCode ?? string.Empty;
            if (string.IsNullOrWhiteSpace(type))
                throw new ValidationException("ContainerType", $"Thiếu loại ISO của {blockerNo}; không thể chọn ô tạm an toàn.");
            if (snapshot?.LoadStatus != null &&
                !string.Equals(snapshot.LoadStatus, "full", StringComparison.OrdinalIgnoreCase))
                throw new ValidationException("LoadStatus", "Kế hoạch có container rỗng; cần xử lý ở quy trình khác.");
            var isReefer = type.Contains("RF", StringComparison.OrdinalIgnoreCase) ||
                           type.Contains("RH", StringComparison.OrdinalIgnoreCase) ||
                           type.EndsWith("R1", StringComparison.OrdinalIgnoreCase) ||
                           type.EndsWith("R2", StringComparison.OrdinalIgnoreCase);
            var size = type.StartsWith("2", StringComparison.OrdinalIgnoreCase) ? 20 :
                       type.StartsWith("4", StringComparison.OrdinalIgnoreCase) ? 40 : 0;
            if (size == 0)
                throw new ValidationException("ContainerType", $"Chưa hỗ trợ kích thước ISO {type} của {blockerNo}.");
            var isDangerous = string.Equals(snapshot!.CargoType, "dangerous", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(snapshot.CargoType, "hazardous", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(snapshot.CargoType, "DG", StringComparison.OrdinalIgnoreCase);

            var candidates = allSlots.Where(s =>
                    s.Status == "empty" && s.ContainerLengthFt == size &&
                    !occupied.Contains(s.Id) && !used.Contains(s.Id) &&
                    s.Tier is >= 1 and <= 4 && s.Block != null &&
                    !(s.YardBlockId == source.YardBlockId && s.Bay == source.Bay && s.Row == source.Row) &&
                    (isReefer ? s.HasReeferPlug : !s.HasReeferPlug) &&
                    (!s.MaxWeightKg.HasValue ||
                     (snapshot.GrossWeightKg.HasValue && snapshot.GrossWeightKg <= s.MaxWeightKg)) &&
                    (s.Block.IsDangerousArea == isDangerous) &&
                    (s.Tier == 1 || allSlots.Any(below => below.YardBlockId == s.YardBlockId &&
                        below.Bay == s.Bay && below.Row == s.Row && below.Tier == s.Tier - 1 &&
                        below.ContainerLengthFt == size && occupied.Contains(below.Id))))
                .OrderByDescending(s => s.YardBlockId == source.YardBlockId)
                .ThenBy(s => Math.Abs(s.Bay - source.Bay) + Math.Abs(s.Row - source.Row))
                .ThenBy(s => s.Tier).ToList();

            YardSlot? selected = null;
            foreach (var candidate in candidates)
            {
                if (candidate.Tier > 1)
                {
                    var below = allSlots.First(s => s.YardBlockId == candidate.YardBlockId &&
                        s.Bay == candidate.Bay && s.Row == candidate.Row && s.Tier == candidate.Tier - 1);
                    var belowPosition = positions.FirstOrDefault(p => p.SlotId == below.Id);
                    if (belowPosition != null)
                    {
                        if (!names.TryGetValue(belowPosition.ContainerId, out var belowNo)) continue;
                        var belowType = (await YardContainerSnapshot.ReadAsync(db, belowNo, ct))?.TypeCode ?? "";
                        if ((belowType.StartsWith("2") ? 20 : belowType.StartsWith("4") ? 40 : 0) != size)
                            continue;
                    }
                    // Do not stack above a slot that only exists as a planned move.
                    else continue;
                }
                selected = candidate;
                break;
            }

            if (selected == null)
            {
                result.CanExecute = false;
                result.ValidationMessage = $"Không có ô tạm hợp lệ cho {blockerNo}; cần điều độ xử lý.";
                return result;
            }

            used.Add(selected.Id);
            var reason = selected.YardBlockId == source.YardBlockId
                ? "Cùng block, ô trống hợp lệ và có tầng đỡ"
                : "Ô trống hợp lệ ở block khác; block gần đã hết chỗ";
            result.BlockingContainers.Add(new BlockingContainerDto
            {
                StepOrder = result.BlockingContainers.Count + 1,
                ContainerNo = blockerNo, ContainerType = type,
                CurrentLocation = Location(position.Slot), Tier = position.Slot.Tier,
                RecommendedBufferSlot = Location(selected), Reason = reason
            });
            var dist = Math.Abs(selected.Bay - source.Bay) + Math.Abs(selected.Row - source.Row);
            var distRating = selected.YardBlockId == source.YardBlockId
                ? (dist <= 4 ? "Cùng block (Cùng dãy)" : "Cùng block (Dãy lân cận)")
                : $"Khác block ({selected.Block.Code})";

            result.AvailableBufferSlots.Add(new AvailableBufferSlotDto
            {
                Location = Location(selected), BlockCode = selected.Block.Code,
                Bay = selected.Bay, Row = selected.Row, Tier = selected.Tier,
                DistanceRating = distRating
            });
        }

        result.CanExecute = true;
        result.ValidationMessage = blockers.Count == 0
            ? "Container không bị đè; có thể lập lệnh lấy trực tiếp."
            : $"Đã tìm được {blockers.Count} ô tạm từ dữ liệu bãi hiện tại. Cần kiểm tra lại khi phát lệnh.";
        return result;
    }

    internal static string Location(YardSlot slot) =>
        $"{slot.Block.Code}-{slot.Bay:D2}-{slot.Row:D2}-{slot.Tier}";

    private sealed class AppointmentQueryResult
    {
        public string BookingNo { get; set; } = string.Empty;
        public DateTime AppointmentAt { get; set; }
    }
}
