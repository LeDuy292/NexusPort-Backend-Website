using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NexusPort.Infrastructure.Database;
using NexusPort.Modules.Yard.Application.DTOs;
using NexusPort.Modules.Yard.Application.Interfaces;
using NexusPort.Modules.Yard.Domain.Entities;
using NexusPort.Shared.Exceptions;

namespace NexusPort.Modules.Yard.Application.Services;

public class YardDischargeService : IYardDischargeService
{
    private readonly AppDbContext _context;
    private readonly ILogger<YardDischargeService> _logger;
    private readonly NexusPort.Infrastructure.ExternalServices.IS3StorageService _storage;

    public YardDischargeService(AppDbContext context, ILogger<YardDischargeService> logger, NexusPort.Infrastructure.ExternalServices.IS3StorageService storage)
    {
        _context = context;
        _logger = logger;
        _storage = storage;
    }

    /// <summary>
    /// Ảnh hư hỏng đến dưới dạng data URL base64 (vài trăm KB mỗi ảnh). Đẩy lên S3 và chỉ lưu khóa đối tượng trong bản khai
    /// để payload không phình; nếu S3 lỗi thì giữ nguyên ảnh gốc để không làm gián đoạn nghiệp vụ.
    /// </summary>
    private async Task<List<string>> StorePhotosAsync(List<string>? photos, CancellationToken cancellationToken)
    {
        var result = new List<string>();
        foreach (var photo in photos ?? new List<string>())
        {
            var m = System.Text.RegularExpressions.Regex.Match(photo ?? "", @"^data:(image/[a-zA-Z0-9.+-]+);base64,(.+)$", System.Text.RegularExpressions.RegexOptions.Singleline);
            if (!m.Success) { result.Add(photo ?? ""); continue; }
            try
            {
                var contentType = m.Groups[1].Value;
                var ext = contentType.EndsWith("png", StringComparison.OrdinalIgnoreCase) ? ".png" : ".jpg";
                var url = await _storage.UploadBytesAsync(Convert.FromBase64String(m.Groups[2].Value), "damage" + ext, contentType, "NexusPort/yard-damage", cancellationToken);
                result.Add(Uri.UnescapeDataString(new Uri(url).AbsolutePath.TrimStart('/')));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Không đẩy được ảnh hư hỏng lên S3, giữ dữ liệu gốc.");
                result.Add(photo ?? "");
            }
        }
        return result;
    }

    /// <summary>Khóa S3 -> link có hạn để giao diện hiển thị; giá trị khác (data URL, URL ngoài) giữ nguyên.</summary>
    private List<string> SignPhotos(IEnumerable<string>? photos) =>
        (photos ?? Enumerable.Empty<string>()).Select(p => _storage.GetPresignedUrl(p)).ToList();

    public async Task<VesselDischargePlanDto> PreAllocateManifestAsync(Guid manifestId, CancellationToken cancellationToken = default)
    {
        var manifestRow = await GetManifestRowAsync(manifestId, cancellationToken);
        if (manifestRow == null)
        {
            throw new NotFoundException("Bản khai", manifestId);
        }

        var payload = manifestRow.Value.Payload;
        var vesselName = payload.TryGetProperty("vesselName", out var vn) ? vn.GetString() ?? "EVER GLORY" : "EVER GLORY";
        var voyageNumber = payload.TryGetProperty("voyageNumber", out var voy) ? voy.GetString() ?? "" : "";
        var berthCode = payload.TryGetProperty("berthCode", out var bc) ? bc.GetString() ?? "B-01" : "B-01";
        var direction = payload.TryGetProperty("direction", out var dir) ? dir.GetString() ?? "IMPORT" : "IMPORT";
        var isExport = string.Equals(direction, "EXPORT", StringComparison.OrdinalIgnoreCase) ||
                       voyageNumber.Trim().EndsWith("E", StringComparison.OrdinalIgnoreCase);
        var manifestDirection = isExport ? "EXPORT" : "IMPORT";

        var billsElement = payload.TryGetProperty("bills", out var bills) ? bills : default;
        var containersToAllocate = ExtractContainers(billsElement, payload);

        if (containersToAllocate.Count == 0)
        {
            return BuildPlanDto(manifestId, payload, containersToAllocate);
        }

        // Lấy danh sách toàn bộ ô bãi đang EMPTY từ Database (AsNoTracking)
        var emptySlots = await _context.Set<YardSlot>()
            .Include(s => s.Block)
            .AsNoTracking()
            .Where(s => s.Status == "empty")
            .OrderBy(s => s.Block.Code)
            .ThenBy(s => s.Bay)
            .ThenBy(s => s.Row)
            .ThenBy(s => s.Tier)
            .ToListAsync(cancellationToken);

        // Sắp xếp thứ tự phân bổ theo chuẩn Quy hoạch bãi 4.5.1:
        // Hàng xuất: POD -> Hãng tàu -> Cỡ loại -> Nhóm trọng lượng (Nặng trước)
        // Hàng nhập FCL: Hãng tàu -> Cỡ loại -> Vận đơn B/L -> Trọng lượng
        // Hàng nhập LCL: Hãng tàu -> Cỡ loại -> Chủ khai thác (Consolidator)
        containersToAllocate.sortContainers(manifestDirection);

        var allocatedSlotIds = new HashSet<Guid>();
        var billAllocations = new Dictionary<string, List<(string Block, int Bay, int Row, int Tier, decimal Weight)>>();
        var carrierAllocations = new Dictionary<string, List<(string Block, int Bay, int Row, int Tier, decimal Weight)>>();
        var podAllocations = new Dictionary<string, List<(string Block, int Bay, int Row, int Tier, decimal Weight)>>();
        var consolidatorAllocations = new Dictionary<string, List<(string Block, int Bay, int Row, int Tier, decimal Weight)>>();
        var stackWeights = new Dictionary<string, decimal>(); // Lưu trọng lượng cont đã hạ theo tọa độ
        var allocatedItems = new List<DischargeContainerItemDto>();

        foreach (var cont in containersToAllocate)
        {
            var bestCandidate = FindBestSlot(
                cont,
                emptySlots,
                allocatedSlotIds,
                billAllocations,
                carrierAllocations,
                podAllocations,
                consolidatorAllocations,
                stackWeights,
                manifestDirection: manifestDirection);

            if (bestCandidate != null)
            {
                var slot = bestCandidate.Value.Slot;
                allocatedSlotIds.Add(slot.Id);

                var slotCoordinate = $"{slot.Block.Code}-{slot.Bay:D2}-{slot.Row:D2}-{slot.Tier}";
                stackWeights[slotCoordinate] = cont.GrossWeightKg;

                // Ghi nhận phân bổ gom nhóm theo 4.5.1
                var bl = cont.BlNumber ?? string.Empty;
                if (!string.IsNullOrEmpty(bl))
                {
                    if (!billAllocations.ContainsKey(bl)) billAllocations[bl] = new();
                    billAllocations[bl].Add((slot.Block.Code, slot.Bay, slot.Row, slot.Tier, cont.GrossWeightKg));
                }

                var carrier = cont.ShippingLine ?? string.Empty;
                if (!string.IsNullOrEmpty(carrier))
                {
                    if (!carrierAllocations.ContainsKey(carrier)) carrierAllocations[carrier] = new();
                    carrierAllocations[carrier].Add((slot.Block.Code, slot.Bay, slot.Row, slot.Tier, cont.GrossWeightKg));
                }

                var pod = cont.Pod ?? string.Empty;
                if (!string.IsNullOrEmpty(pod))
                {
                    if (!podAllocations.ContainsKey(pod)) podAllocations[pod] = new();
                    podAllocations[pod].Add((slot.Block.Code, slot.Bay, slot.Row, slot.Tier, cont.GrossWeightKg));
                }

                var cons = cont.Consolidator ?? string.Empty;
                if (!string.IsNullOrEmpty(cons))
                {
                    if (!consolidatorAllocations.ContainsKey(cons)) consolidatorAllocations[cons] = new();
                    consolidatorAllocations[cons].Add((slot.Block.Code, slot.Bay, slot.Row, slot.Tier, cont.GrossWeightKg));
                }

                cont.ReservedSlot = slotCoordinate;
                cont.ReservedSlotId = slot.Id;
                cont.IsPreAllocated = true;
                cont.AllocationScore = bestCandidate.Value.Score;
                cont.AllocationReason = $"Quy hoạch bãi (Mục 4.5.1) Block {slot.Block.Code}: {bestCandidate.Value.Reason}";
                cont.DischargeStatus = "PLANNED";

                allocatedItems.Add(cont);
            }
            else
            {
                _logger.LogWarning("[PreAllocate] Không tìm thấy ô phù hợp cho container {ContNo}", cont.ContainerNumber);
                cont.AllocationReason = "Chưa tìm được ô bãi thỏa mãn tiêu chuẩn 4.5.1; chuyển sang hàng đợi phân bổ ngoại lệ.";
            }
        }

        // Lưu trạng thái 'reserved' cho các ô bãi trong Database qua direct command
        if (allocatedSlotIds.Count > 0)
        {
            await _context.Database.OpenConnectionAsync(cancellationToken);
            try
            {
                var conn = _context.Database.GetDbConnection();
                using var slotCmd = conn.CreateCommand();
                slotCmd.CommandText = "UPDATE yard_slots SET status = 'reserved'::yard_slot_status WHERE id = ANY(@ids)";
                var pIds = slotCmd.CreateParameter();
                pIds.ParameterName = "@ids";
                pIds.Value = allocatedSlotIds.ToArray();
                slotCmd.Parameters.Add(pIds);
                await slotCmd.ExecuteNonQueryAsync(cancellationToken);
            }
            finally
            {
                await _context.Database.CloseConnectionAsync();
            }
        }

        // Cập nhật lại payload của shipping_manifests với vị trí đã gán
        await UpdateManifestPayloadWithAllocationsAsync(manifestId, payload, allocatedItems, cancellationToken);

        // Đồng bộ các nhiệm vụ dỡ tàu (YardTask) vào bảng yard_tasks thực tế trong CSDL
        await SyncYardTasksForDischargePlanAsync(manifestId, vesselName, voyageNumber, berthCode, allocatedItems, cancellationToken);

        _logger.LogInformation("[PreAllocate] Đã phân bổ trước {Count}/{Total} container cho Manifest {Id}", allocatedItems.Count, containersToAllocate.Count, manifestId);

        return BuildPlanDto(manifestId, payload, containersToAllocate);
    }

    public async Task<IReadOnlyList<VesselDischargePlanDto>> GetActiveDischargePlansAsync(CancellationToken cancellationToken = default)
    {
        await _context.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var conn = _context.Database.GetDbConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT id, payload
                FROM shipping_manifests
                WHERE payload->>'status' IN ('PORT_APPROVED', 'DISCHARGING', 'SENT_TO_PORT')
                ORDER BY created_at DESC
                LIMIT 20";

            var plans = new List<VesselDischargePlanDto>();
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetGuid(0);
                var rawJson = reader.GetString(1);
                using var doc = JsonDocument.Parse(rawJson);
                var payload = doc.RootElement.Clone();
                var billsElement = payload.TryGetProperty("bills", out var bills) ? bills : default;
                var containers = ExtractContainers(billsElement, payload);
                plans.Add(BuildPlanDto(id, payload, containers));
            }

            return plans;
        }
        finally
        {
            await _context.Database.CloseConnectionAsync();
        }
    }

    public async Task<VesselDischargePlanDto?> GetDischargePlanByIdAsync(Guid manifestId, CancellationToken cancellationToken = default)
    {
        var manifestRow = await GetManifestRowAsync(manifestId, cancellationToken);
        if (manifestRow == null) return null;

        var payload = manifestRow.Value.Payload;
        var billsElement = payload.TryGetProperty("bills", out var bills) ? bills : default;
        var containers = ExtractContainers(billsElement, payload);
        return BuildPlanDto(manifestId, payload, containers);
    }

    public async Task<TallyScanResultDto> TallyScanAsync(TallyScanRequestDto dto, string inspectorName, CancellationToken cancellationToken = default)
    {
        var cleanNo = (dto.ContainerNumber ?? string.Empty).Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(cleanNo))
        {
            throw new ValidationException("ContainerNumber", "Vui lòng cung cấp số container.");
        }

        Guid? matchedManifestId = null;
        JsonElement targetPayload = default;
        DischargeContainerItemDto? matchedContainer = null;
        string? matchedVessel = null;
        string? matchedVoyage = null;

        await _context.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var conn = _context.Database.GetDbConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT id, payload
                FROM shipping_manifests
                WHERE payload->>'status' IN ('PORT_APPROVED', 'DISCHARGING', 'SENT_TO_PORT')
                ORDER BY created_at DESC";

            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetGuid(0);
                var rawJson = reader.GetString(1);
                using var doc = JsonDocument.Parse(rawJson);
                var payload = doc.RootElement.Clone();
                var billsElement = payload.TryGetProperty("bills", out var bills) ? bills : default;
                var containers = ExtractContainers(billsElement, payload);

                var match = containers.FirstOrDefault(c => string.Equals(c.ContainerNumber, cleanNo, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    matchedManifestId = id;
                    targetPayload = payload;
                    matchedContainer = match;
                    matchedVessel = payload.TryGetProperty("vesselName", out var vn) ? vn.GetString() : "Tàu nhập khẩu";
                    matchedVoyage = payload.TryGetProperty("voyageNumber", out var vyn) ? vyn.GetString() : "";
                    break;
                }
            }
        }
        finally
        {
            await _context.Database.CloseConnectionAsync();
        }

        if (matchedContainer != null && matchedManifestId.HasValue)
        {
            // Container HỢP LỆ trong Manifest
            matchedContainer.DischargedAt = matchedContainer.DischargedAt ?? DateTime.UtcNow;
            matchedContainer.Condition = dto.Condition ?? "GOOD";
            matchedContainer.InspectionNotes = dto.Notes ?? "";
            matchedContainer.DischargeStatus = dto.ConfirmPutdown ? "IN_YARD" : "DISCHARGED";
            if (dto.DamagePhotos != null && dto.DamagePhotos.Count > 0)
            {
                dto.DamagePhotos = await StorePhotosAsync(dto.DamagePhotos, cancellationToken);
                matchedContainer.DamagePhotos = dto.DamagePhotos;
            }

            var finalSlot = !string.IsNullOrEmpty(dto.ActualSlot) ? dto.ActualSlot : matchedContainer.ReservedSlot;

            // Cập nhật lại DB (kèm ảnh chụp hư hỏng nếu có)
            await UpdateManifestContainerStatusAsync(matchedManifestId.Value, cleanNo, matchedContainer.DischargeStatus, matchedContainer.DischargedAt.Value, matchedContainer.Condition, matchedContainer.InspectionNotes, finalSlot, dto.DamagePhotos, cancellationToken);

            if (dto.ConfirmPutdown && !string.IsNullOrEmpty(finalSlot))
            {
                try
                {
                    await MarkSlotOccupiedAsync(finalSlot, cleanNo, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[TallyScan] Không thể đánh dấu slot occupied cho container {CleanNo}", cleanNo);
                }
            }

            // Đồng bộ trạng thái vào bảng yard_tasks
            if (!string.IsNullOrEmpty(finalSlot))
            {
                await UpdateYardTaskOnTallyScanAsync(cleanNo, finalSlot, dto.ConfirmPutdown, dto.Condition, cancellationToken);
            }

            return new TallyScanResultDto
            {
                IsMatch = true,
                IsException = false,
                ContainerNumber = cleanNo,
                ManifestId = matchedManifestId,
                VesselName = matchedVessel,
                VoyageNumber = matchedVoyage,
                BlNumber = matchedContainer.BlNumber,
                Consignee = matchedContainer.Consignee,
                ReservedSlot = finalSlot,
                AllocationScore = matchedContainer.AllocationScore,
                AllocationReason = matchedContainer.AllocationReason ?? "Đã đặt chỗ trước theo Manifest",
                DischargeStatus = matchedContainer.DischargeStatus,
                DischargedAt = matchedContainer.DischargedAt,
                Condition = matchedContainer.Condition,
                IsReefer = matchedContainer.IsReefer,
                WeightCategory = matchedContainer.WeightCategory,
                CargoType = matchedContainer.CargoType,
                ShippingLine = matchedContainer.ShippingLine,
                Pod = matchedContainer.Pod,
                Consolidator = matchedContainer.Consolidator,
                DamagePhotos = SignPhotos(matchedContainer.DamagePhotos),
                Message = dto.ConfirmPutdown
                    ? $"Xác nhận hoàn tất: Container {cleanNo} đã hạ bãi an toàn tại ô {finalSlot} (Trạng thái: IN YARD)."
                    : $"Đối chiếu thành công: Container {cleanNo} thuộc tàu {matchedVessel}. Vị trí đã đặt trước: {finalSlot}."
            };
        }
        else
        {
            // NGOẠI LỆ: Container KHÔNG có trong Manifest
            return new TallyScanResultDto
            {
                IsMatch = false,
                IsException = true,
                ContainerNumber = cleanNo,
                AssignedBufferSlot = "BUFFER-S01-01-1",
                DischargeStatus = "EXCEPTION",
                DamagePhotos = SignPhotos(await StorePhotosAsync(dto.DamagePhotos, cancellationToken)),
                Message = $"CẢNH BÁO: Container {cleanNo} KHÔNG TÌM THẤY trong bất kỳ Manifest nhập nào! Hệ thống đã đưa vào hàng đợi ngoại lệ và tự động gán khu cách ly bãi tạm BUFFER-S01.",
                SuggestedAction = "Báo điều độ viên liên hệ Hãng tàu gửi văn bản xác minh và kiểm tra số seal thực tế."
            };
        }
    }

    public async Task<VesselDischargePlanDto> OverrideSlotAsync(OverrideSlotRequestDto dto, CancellationToken cancellationToken = default)
    {
        var manifestRow = await GetManifestRowAsync(dto.ManifestId, cancellationToken);
        if (manifestRow == null) throw new NotFoundException("Bản khai", dto.ManifestId);

        var payload = manifestRow.Value.Payload;
        var billsElement = payload.TryGetProperty("bills", out var bills) ? bills : default;
        var containers = ExtractContainers(billsElement, payload);

        var match = containers.FirstOrDefault(c => string.Equals(c.ContainerNumber, dto.ContainerNumber, StringComparison.OrdinalIgnoreCase));
        if (match != null)
        {
            match.ReservedSlot = dto.NewSlotCoordinate;
            match.AllocationReason = $"Điều độ điều chỉnh thủ công: {dto.Reason ?? "Theo chỉ đạo hiện trường"}";
            await UpdateManifestPayloadWithAllocationsAsync(dto.ManifestId, payload, containers, cancellationToken);

            var vesselName = payload.TryGetProperty("vesselName", out var vn) ? vn.GetString() ?? "Vessel" : "Vessel";
            var voyageNumber = payload.TryGetProperty("voyageNumber", out var voy) ? voy.GetString() ?? "Voyage" : "Voyage";
            var berthCode = payload.TryGetProperty("berth", out var bth) ? bth.GetString() ?? "B-01" : "B-01";
            await SyncYardTasksForDischargePlanAsync(dto.ManifestId, vesselName, voyageNumber, berthCode, containers, cancellationToken);
        }

        return BuildPlanDto(dto.ManifestId, payload, containers);
    }

    #region Private Helper Methods

    private (YardSlot Slot, int Score, string Reason)? FindBestSlot(
        DischargeContainerItemDto cont,
        List<YardSlot> emptySlots,
        HashSet<Guid> reservedIds,
        Dictionary<string, List<(string Block, int Bay, int Row, int Tier, decimal Weight)>> billAllocations,
        Dictionary<string, List<(string Block, int Bay, int Row, int Tier, decimal Weight)>> carrierAllocations,
        Dictionary<string, List<(string Block, int Bay, int Row, int Tier, decimal Weight)>> podAllocations,
        Dictionary<string, List<(string Block, int Bay, int Row, int Tier, decimal Weight)>> consolidatorAllocations,
        Dictionary<string, decimal> stackWeights,
        string manifestDirection = "IMPORT")
    {
        var is20ft = (cont.IsoType ?? "").StartsWith("20");
        var isHeavy = cont.WeightCategory == "HEAVY" || cont.GrossWeightKg > 20000m;
        var isLight = cont.WeightCategory == "LIGHT" || cont.GrossWeightKg < 12000m;

        YardSlot? bestSlot = null;
        var maxScore = -1;
        var bestReason = "";

        foreach (var slot in emptySlots)
        {
            if (reservedIds.Contains(slot.Id)) continue;

            var blockCode = slot.Block?.Code ?? "";
            var isReeferBlock = slot.HasReeferPlug || (slot.Block != null && slot.Block.IsReeferArea) || blockCode.StartsWith("D") || blockCode == "R";

            // ===== 1. CỠ VÀ CHỦNG LOẠI CONTAINER (Mục 4.5.1) =====
            // Cont lạnh (Reefer): Bắt buộc vào khu lạnh/có ổ cắm điện
            if (cont.IsReefer)
            {
                if (!isReeferBlock) continue;
            }
            else
            {
                if (isReeferBlock) continue; // Cont thường KHÔNG chiếm ô bãi lạnh
            }

            // Kích thước: Bay lẻ = 20ft; Bay chẵn = 40ft/45ft
            if (is20ft && slot.Bay % 2 == 0) continue;
            if (!is20ft && slot.Bay % 2 != 0) continue;

            // Max weight giới hạn tải của slot
            if (slot.MaxWeightKg.HasValue && cont.GrossWeightKg > slot.MaxWeightKg.Value) continue;

            // ===== 2. NGUYÊN TẮC CHẤT XẾP & NHÓM TRỌNG LƯỢNG (Mục 4.5.1) =====
            // Nhóm Heavy (> 20T): Chỉ được xếp ở Tầng 1 hoặc tối đa Tầng 2. Tránh xếp từ Tầng 3 trở lên.
            if (isHeavy && slot.Tier >= 3) continue;

            // Kiểm tra tầng đỡ vật lý: Tầng N > 1 phải có tầng N-1 bên dưới
            if (slot.Tier > 1)
            {
                var lowerSlotKey = $"{blockCode}-{slot.Bay:D2}-{slot.Row:D2}-{slot.Tier - 1}";
                
                // Kiểm tra xem tầng dưới đã có container trong stackWeights hoặc trong DB chưa
                var lowerTierSlot = emptySlots.FirstOrDefault(es =>
                    es.Block?.Code == blockCode && es.Bay == slot.Bay && es.Row == slot.Row && es.Tier == slot.Tier - 1);
                
                if (lowerTierSlot != null && !reservedIds.Contains(lowerTierSlot.Id))
                {
                    // Tầng dưới vẫn đang empty và chưa ai reserve -> không thể đặt lên tầng này
                    continue;
                }

                // Nguyên tắc "Nặng dưới, Nhẹ trên": Container tầng trên không được nặng hơn tầng dưới quá 1000kg
                if (stackWeights.TryGetValue(lowerSlotKey, out var lowerWeight))
                {
                    if (cont.GrossWeightKg > lowerWeight + 1000m)
                    {
                        // Vi phạm nguyên tắc xếp nặng dưới - nhẹ trên
                        continue;
                    }
                }
            }

            // ===== 3. CHẤM ĐIỂM THEO NGUYÊN TẮC CHUNG 4.5.1 =====
            var score = 50;
            var reasons = new List<string>();

            var isNearGateBlock = blockCode.StartsWith("F") || blockCode.StartsWith("E") || blockCode == "B04" || blockCode == "B03" || blockCode == "C09" || blockCode == "C10" || (slot.Block?.IsNearGate == true);
            var isNearSeaBlock = blockCode.StartsWith("A") || blockCode == "B01" || blockCode == "B02" || blockCode == "C01" || blockCode == "C02" || blockCode == "C03" || blockCode == "C04";

            if (manifestDirection == "EXPORT")
            {
                // ----- A. CONTAINER HÀNG XUẤT (EXPORT) (Mục 4.5.1) -----
                // Vị trí bãi: Xe ngoài chở vào tập kết -> Ưu tiên khu gần cổng Gate
                if (isNearSeaBlock) continue; // Cấm xếp gần biển gây nghẽn cầu tàu

                if (isNearGateBlock)
                {
                    score += 40;
                    reasons.Add($"Bãi tập kết hàng xuất gần cổng Block {blockCode} (+40đ)");
                }

                // 1. Phân tách theo Cảng dỡ hàng (POD) & Chuyến tàu
                var pod = cont.Pod ?? "";
                if (!string.IsNullOrEmpty(pod) && podAllocations.TryGetValue(pod, out var podList))
                {
                    if (podList.Any(p => p.Block == blockCode && Math.Abs(p.Bay - slot.Bay) <= 2))
                    {
                        score += 60;
                        reasons.Add($"Cùng cụm Bay cảng dỡ {pod} (+60đ)");
                    }
                    else if (podList.Any(p => p.Block == blockCode))
                    {
                        score += 30;
                        reasons.Add($"Cùng Block cảng dỡ {pod} (+30đ)");
                    }
                }

                // 2. Sắp xếp theo Hãng tàu hoặc tuyến đường
                var carrier = cont.ShippingLine ?? "";
                if (!string.IsNullOrEmpty(carrier) && carrierAllocations.TryGetValue(carrier, out var carList))
                {
                    if (carList.Any(c => c.Block == blockCode))
                    {
                        score += 35;
                        reasons.Add($"Quy hoạch theo hãng tàu {carrier} (+35đ)");
                    }
                }

                // 3. Phân nhóm theo trọng lượng
                if (isHeavy && slot.Tier == 1)
                {
                    score += 30;
                    reasons.Add("Nhóm Heavy (Nặng) xếp Tầng 1 theo chuẩn chất xếp (+30đ)");
                }
                else if (isLight && slot.Tier >= 3)
                {
                    score += 25;
                    reasons.Add("Nhóm Light (Nhẹ) xếp Tầng cao theo chuẩn chất xếp (+25đ)");
                }
                else if (!isHeavy && !isLight && (slot.Tier == 2 || slot.Tier == 3))
                {
                    score += 20;
                    reasons.Add("Nhóm Medium (Vừa) xếp Tầng 2-3 chuẩn ổn định (+20đ)");
                }
            }
            else
            {
                // ----- B. CONTAINER HÀNG NHẬP (IMPORT) (Mục 4.5.1) -----
                // Vị trí bãi: Dỡ từ tàu cập bến -> Ưu tiên gần biển / cầu cảng
                if (isNearSeaBlock)
                {
                    score += 45;
                    reasons.Add($"Gần cầu cảng/cẩu bờ bốc dỡ Block {blockCode} (+45đ)");
                }
                else if (isNearGateBlock && cont.CargoType != "LCL")
                {
                    score -= 25; // Hàng nhập nguyên cont FCL không nên để ngoài cổng
                }

                // 1. Phân tách theo Hãng tàu
                var carrier = cont.ShippingLine ?? "";
                if (!string.IsNullOrEmpty(carrier) && carrierAllocations.TryGetValue(carrier, out var carList))
                {
                    if (carList.Any(c => c.Block == blockCode))
                    {
                        score += 35;
                        reasons.Add($"Nhóm theo hãng tàu {carrier} (+35đ)");
                    }
                }

                if (cont.CargoType == "LCL")
                {
                    // 2. Hàng LCL: Phân tách theo Hãng tàu, cỡ/loại, CHỦ KHAI THÁC (Consolidator)
                    var cons = cont.Consolidator ?? "";
                    var isNearCfsBlock = blockCode.StartsWith("E") || blockCode.StartsWith("F") || blockCode.StartsWith("G");
                    if (isNearCfsBlock)
                    {
                        score += 50;
                        reasons.Add($"Khu bãi gần kho CFS phục vụ rút ruột LCL (+50đ)");
                    }
                    if (!string.IsNullOrEmpty(cons) && consolidatorAllocations.TryGetValue(cons, out var consList))
                    {
                        if (consList.Any(c => c.Block == blockCode))
                        {
                            score += 40;
                            reasons.Add($"Khu vực riêng cho chủ khai thác LCL {cons} (+40đ)");
                        }
                    }
                }
                else
                {
                    // 3. Hàng FCL: Gom theo Vận đơn (B/L) cho lô hàng lớn
                    var bl = cont.BlNumber ?? "";
                    if (!string.IsNullOrEmpty(bl) && billAllocations.TryGetValue(bl, out var existing))
                    {
                        var sameBay = existing.Any(e => e.Block == blockCode && Math.Abs(e.Bay - slot.Bay) <= 2);
                        var sameBlock = existing.Any(e => e.Block == blockCode);
                        if (sameBay)
                        {
                            score += 65;
                            reasons.Add($"Gom cùng lô B/L {bl} để lấy nguyên lô thuận tiện (+65đ)");
                        }
                        else if (sameBlock)
                        {
                            score += 30;
                            reasons.Add($"Cùng Block với lô B/L {bl} (+30đ)");
                        }
                    }
                }

                // Nhóm trọng lượng hàng nhập
                if (isHeavy && slot.Tier == 1)
                {
                    score += 25;
                    reasons.Add("Cont nặng hạ Tầng 1 an toàn bãi (+25đ)");
                }
                else if (isLight && slot.Tier >= 2)
                {
                    score += 15;
                    reasons.Add("Cont nhẹ xếp Tầng trên (+15đ)");
                }
            }

            // Cont lạnh
            if (cont.IsReefer && slot.HasReeferPlug)
            {
                score += 30;
                reasons.Add("Cắm điện Reefer duy trì nhiệt độ (+30đ)");
            }

            // Cân bằng tải Block
            var maxCap = slot.Block?.MaxCapacity ?? 0;
            var currOcc = slot.Block?.CurrentOccupancy ?? 0;
            var occupancy = maxCap > 0 ? (double)currOcc / maxCap : 0;
            if (occupancy < 0.6)
            {
                score += 15;
                reasons.Add($"Cân bằng tải Block {blockCode} (+15đ)");
            }

            if (score > maxScore)
            {
                maxScore = score;
                bestSlot = slot;
                bestReason = reasons.Count > 0 ? string.Join("; ", reasons) : "Đạt chuẩn quy hoạch bãi 4.5.1";
            }
        }

        return bestSlot != null ? (bestSlot, maxScore, bestReason) : null;
    }

    private List<DischargeContainerItemDto> ExtractContainers(JsonElement billsElement, JsonElement? rootPayload = null)
    {
        var list = new List<DischargeContainerItemDto>();
        if (billsElement.ValueKind != JsonValueKind.Array) return list;

        var defaultShippingLine = "EVERGREEN";
        if (rootPayload.HasValue)
        {
            if (rootPayload.Value.TryGetProperty("carrierCode", out var cc) && !string.IsNullOrWhiteSpace(cc.GetString()))
                defaultShippingLine = cc.GetString()!;
            else if (rootPayload.Value.TryGetProperty("shippingLine", out var slr) && !string.IsNullOrWhiteSpace(slr.GetString()))
                defaultShippingLine = slr.GetString()!;
            else if (rootPayload.Value.TryGetProperty("vesselName", out var vn) && vn.GetString()?.Contains("MAERSK", StringComparison.OrdinalIgnoreCase) == true)
                defaultShippingLine = "MAERSK";
            else if (rootPayload.Value.TryGetProperty("vesselName", out var vn2) && vn2.GetString()?.Contains("ONE", StringComparison.OrdinalIgnoreCase) == true)
                defaultShippingLine = "ONE";
        }

        foreach (var bill in billsElement.EnumerateArray())
        {
            var blNumber = bill.TryGetProperty("blNumber", out var bl) ? bl.GetString() : "";
            var consignee = bill.TryGetProperty("consignee", out var cs) ? cs.GetString() : "";
            var billCarrier = bill.TryGetProperty("carrier", out var bcr) && !string.IsNullOrWhiteSpace(bcr.GetString())
                ? bcr.GetString()
                : (bill.TryGetProperty("shippingLine", out var bsl) && !string.IsNullOrWhiteSpace(bsl.GetString()) ? bsl.GetString() : defaultShippingLine);
            var billPod = bill.TryGetProperty("pod", out var bpod) && !string.IsNullOrWhiteSpace(bpod.GetString())
                ? bpod.GetString()
                : (bill.TryGetProperty("portOfDischarge", out var bpod2) && !string.IsNullOrWhiteSpace(bpod2.GetString()) ? bpod2.GetString() : "VNDAN");
            var billCargoType = bill.TryGetProperty("cargoType", out var bct) && !string.IsNullOrWhiteSpace(bct.GetString())
                ? bct.GetString()!
                : (bill.TryGetProperty("loadType", out var blt) && !string.IsNullOrWhiteSpace(blt.GetString()) ? blt.GetString()! : "FCL");
            var billConsolidator = bill.TryGetProperty("consolidator", out var bcsld) && !string.IsNullOrWhiteSpace(bcsld.GetString())
                ? bcsld.GetString()
                : (bill.TryGetProperty("cfsOperator", out var bcfs) && !string.IsNullOrWhiteSpace(bcfs.GetString()) ? bcfs.GetString() : (billCargoType.Equals("LCL", StringComparison.OrdinalIgnoreCase) ? "DANANG_CFS_01" : null));

            if (bill.TryGetProperty("containers", out var containers) && containers.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in containers.EnumerateArray())
                {
                    var contNo = c.TryGetProperty("containerNumber", out var cn) ? cn.GetString() : "";
                    if (string.IsNullOrEmpty(contNo)) continue;

                    var isoType = c.TryGetProperty("isoType", out var it) ? it.GetString() : "40HC";
                    var isReefer = (c.TryGetProperty("isReefer", out var ir) && ir.GetBoolean()) || (isoType?.Contains("R") == true);
                    var weight = c.TryGetProperty("grossWeightKg", out var gw) && gw.TryGetDecimal(out var w) ? w : 15000m;
                    var commodity = c.TryGetProperty("commodity", out var cm) ? cm.GetString() : "";
                    var seal = c.TryGetProperty("sealNumber", out var sl) ? sl.GetString() : "";
                    var isPreAllocated = (c.TryGetProperty("isPreAllocated", out var ipa) && ipa.GetBoolean())
                        || (c.TryGetProperty("allocationReason", out var arCheck) && arCheck.GetString()?.Contains("Đã kiểm tra tại Khu A") == true);
                    var reservedSlot = c.TryGetProperty("reservedSlot", out var rs) ? rs.GetString() : null;
                    var allocationScore = c.TryGetProperty("allocationScore", out var sc) && sc.TryGetInt32(out var s) ? s : 0;
                    var allocationReason = c.TryGetProperty("allocationReason", out var ar) ? ar.GetString() : null;

                    var contCarrier = c.TryGetProperty("shippingLine", out var csl) && !string.IsNullOrWhiteSpace(csl.GetString()) ? csl.GetString() : billCarrier;
                    var contPod = c.TryGetProperty("pod", out var cpod) && !string.IsNullOrWhiteSpace(cpod.GetString()) ? cpod.GetString() : billPod;
                    var contCargoType = c.TryGetProperty("cargoType", out var cct) && !string.IsNullOrWhiteSpace(cct.GetString()) ? cct.GetString()! : billCargoType;
                    var contConsolidator = c.TryGetProperty("consolidator", out var ccs) && !string.IsNullOrWhiteSpace(ccs.GetString()) ? ccs.GetString() : billConsolidator;

                    var weightCat = weight switch
                    {
                        < 12000m => "LIGHT",
                        <= 20000m => "MEDIUM",
                        _ => "HEAVY"
                    };

                    if (!isPreAllocated || string.IsNullOrEmpty(reservedSlot))
                    {
                        var idx = list.Count;
                        var is20 = (isoType ?? "").StartsWith("20");
                        var bay = is20 ? (1 + (idx % 16) * 2) : (2 + (idx % 8) * 4);
                        var row = 1 + (idx % 4);
                        reservedSlot = $"A01-{bay:D2}-{row:D2}-1";
                        allocationScore = 50;
                        allocationReason = "Tập kết tạm tại Khu A sau khi dỡ tàu - Chờ kiểm tra Tally & đối soát trước khi phân bổ";
                        isPreAllocated = false;
                    }

                    var dischargeStatus = c.TryGetProperty("dischargeStatus", out var ds) ? ds.GetString() : "PLANNED";
                    var condition = c.TryGetProperty("condition", out var cd) ? cd.GetString() : "GOOD";
                    var inspectionNotes = c.TryGetProperty("inspectionNotes", out var inot) ? inot.GetString() : null;
                    var damagePhotos = new List<string>();
                    if (c.TryGetProperty("damagePhotos", out var dp) && dp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var photo in dp.EnumerateArray())
                        {
                            var url = photo.GetString();
                            if (!string.IsNullOrEmpty(url)) damagePhotos.Add(url);
                        }
                    }

                    DateTime? dischargedAt = null;
                    if (c.TryGetProperty("dischargedAt", out var da) && da.TryGetDateTime(out var dt))
                    {
                        dischargedAt = dt;
                    }

                    list.Add(new DischargeContainerItemDto
                    {
                        ContainerNumber = contNo,
                        IsoType = isoType,
                        IsReefer = isReefer,
                        GrossWeightKg = weight,
                        WeightCategory = weightCat,
                        CargoType = contCargoType,
                        ShippingLine = contCarrier,
                        Pod = contPod,
                        Consolidator = contConsolidator,
                        BlNumber = blNumber,
                        Consignee = consignee,
                        Commodity = commodity,
                        SealNumber = seal,
                        ReservedSlot = reservedSlot,
                        IsPreAllocated = isPreAllocated,
                        AllocationScore = allocationScore,
                        AllocationReason = allocationReason,
                        DischargeStatus = dischargeStatus ?? "PLANNED",
                        DischargedAt = dischargedAt,
                        Condition = condition,
                        InspectionNotes = inspectionNotes,
                        DamagePhotos = SignPhotos(damagePhotos)
                    });
                }
            }
        }
        return list;
    }

    private VesselDischargePlanDto BuildPlanDto(Guid manifestId, JsonElement payload, List<DischargeContainerItemDto> containers)
    {
        var manifestNo = payload.TryGetProperty("manifestNumber", out var mn) ? mn.GetString() ?? "" : "";
        var vesselName = payload.TryGetProperty("vesselName", out var vn) ? vn.GetString() ?? "" : "";
        var voyageNo = payload.TryGetProperty("voyageNumber", out var vyn) ? vyn.GetString() ?? "" : "";
        var direction = payload.TryGetProperty("direction", out var dr) ? dr.GetString() ?? "IMPORT" : "IMPORT";
        var status = payload.TryGetProperty("status", out var st) ? st.GetString() ?? "PORT_APPROVED" : "PORT_APPROVED";
        var isPlanPreAllocated = containers.Any(c => c.IsPreAllocated) || (payload.TryGetProperty("isPreAllocated", out var ipa) && ipa.GetBoolean());

        return new VesselDischargePlanDto
        {
            ManifestId = manifestId,
            ManifestNumber = manifestNo,
            VesselName = vesselName,
            VoyageNumber = voyageNo,
            BerthCode = "B-01",
            Direction = direction,
            Status = status,
            TotalContainers = containers.Count,
            TotalWeightKg = containers.Sum(c => c.GrossWeightKg),
            ReeferCount = containers.Count(c => c.IsReefer),
            TwentyFtCount = containers.Count(c => (c.IsoType ?? "").StartsWith("20")),
            FortyFtCount = containers.Count(c => !(c.IsoType ?? "").StartsWith("20")),
            AllocatedCount = containers.Count(c => c.IsPreAllocated && !string.IsNullOrEmpty(c.ReservedSlot)),
            IsPreAllocated = isPlanPreAllocated,
            DischargedCount = containers.Count(c => c.DischargeStatus == "DISCHARGED" || c.DischargeStatus == "IN_YARD"),
            InYardCount = containers.Count(c => c.DischargeStatus == "IN_YARD"),
            Containers = containers
        };
    }

    private async Task<(Guid Id, JsonElement Payload)?> GetManifestRowAsync(Guid manifestId, CancellationToken cancellationToken)
    {
        await _context.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var conn = _context.Database.GetDbConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, payload FROM shipping_manifests WHERE id = @id";
            var param = cmd.CreateParameter();
            param.ParameterName = "@id";
            param.Value = manifestId;
            cmd.Parameters.Add(param);

            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var rawJson = reader.GetString(1);
                using var doc = JsonDocument.Parse(rawJson);
                return (manifestId, doc.RootElement.Clone());
            }
            return null;
        }
        finally
        {
            await _context.Database.CloseConnectionAsync();
        }
    }

    private async Task UpdateManifestPayloadWithAllocationsAsync(Guid manifestId, JsonElement payload, List<DischargeContainerItemDto> allocations, CancellationToken cancellationToken)
    {
        var allocDict = allocations.ToDictionary(a => a.ContainerNumber, StringComparer.OrdinalIgnoreCase);

        // Parse JSON mutable representation
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var prop in payload.EnumerateObject())
            {
                if (prop.NameEquals("bills") && prop.Value.ValueKind == JsonValueKind.Array)
                {
                    writer.WriteStartArray("bills");
                    foreach (var bill in prop.Value.EnumerateArray())
                    {
                        writer.WriteStartObject();
                        foreach (var bProp in bill.EnumerateObject())
                        {
                            if (bProp.NameEquals("containers") && bProp.Value.ValueKind == JsonValueKind.Array)
                            {
                                writer.WriteStartArray("containers");
                                foreach (var cont in bProp.Value.EnumerateArray())
                                {
                                    writer.WriteStartObject();
                                    string contNo = "";
                                    foreach (var cProp in cont.EnumerateObject())
                                    {
                                        if (cProp.NameEquals("containerNumber")) contNo = cProp.Value.GetString() ?? "";
                                        if (cProp.NameEquals("reservedSlot") || cProp.NameEquals("allocationScore") || cProp.NameEquals("allocationReason") || cProp.NameEquals("dischargeStatus") || cProp.NameEquals("isPreAllocated"))
                                            continue;
                                        cProp.WriteTo(writer);
                                    }
                                    if (allocDict.TryGetValue(contNo, out var allocated))
                                    {
                                        writer.WriteString("reservedSlot", allocated.ReservedSlot);
                                        writer.WriteBoolean("isPreAllocated", true);
                                        writer.WriteNumber("allocationScore", allocated.AllocationScore);
                                        writer.WriteString("allocationReason", allocated.AllocationReason);
                                        writer.WriteString("dischargeStatus", allocated.DischargeStatus);
                                    }
                                    writer.WriteEndObject();
                                }
                                writer.WriteEndArray();
                            }
                            else
                            {
                                bProp.WriteTo(writer);
                            }
                        }
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
                else
                {
                    prop.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }

        var updatedJson = System.Text.Encoding.UTF8.GetString(stream.ToArray());

        await _context.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var conn = _context.Database.GetDbConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE shipping_manifests SET payload = @payload::jsonb, revision = revision + 1 WHERE id = @id";
            var pJson = cmd.CreateParameter();
            pJson.ParameterName = "@payload";
            pJson.Value = updatedJson;
            cmd.Parameters.Add(pJson);

            var pId = cmd.CreateParameter();
            pId.ParameterName = "@id";
            pId.Value = manifestId;
            cmd.Parameters.Add(pId);

            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            await _context.Database.CloseConnectionAsync();
        }
    }

    private async Task UpdateManifestContainerStatusAsync(Guid manifestId, string containerNumber, string status, DateTime dischargedAt, string condition, string notes, string? slot, List<string>? damagePhotos, CancellationToken cancellationToken)
    {
        var manifestRow = await GetManifestRowAsync(manifestId, cancellationToken);
        if (manifestRow == null) return;

        var payload = manifestRow.Value.Payload;
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var prop in payload.EnumerateObject())
            {
                if (prop.NameEquals("status"))
                {
                    writer.WriteString("status", "DISCHARGING");
                    continue;
                }
                if (prop.NameEquals("bills") && prop.Value.ValueKind == JsonValueKind.Array)
                {
                    writer.WriteStartArray("bills");
                    foreach (var bill in prop.Value.EnumerateArray())
                    {
                        writer.WriteStartObject();
                        foreach (var bProp in bill.EnumerateObject())
                        {
                            if (bProp.NameEquals("containers") && bProp.Value.ValueKind == JsonValueKind.Array)
                            {
                                writer.WriteStartArray("containers");
                                foreach (var cont in bProp.Value.EnumerateArray())
                                {
                                    writer.WriteStartObject();
                                    var isTarget = false;
                                    foreach (var cProp in cont.EnumerateObject())
                                    {
                                        if (cProp.NameEquals("containerNumber") && string.Equals(cProp.Value.GetString(), containerNumber, StringComparison.OrdinalIgnoreCase))
                                        {
                                            isTarget = true;
                                        }
                                        if (isTarget && (cProp.NameEquals("dischargeStatus") || cProp.NameEquals("dischargedAt") || cProp.NameEquals("condition") || cProp.NameEquals("inspectionNotes") || cProp.NameEquals("damagePhotos") || (slot != null && cProp.NameEquals("reservedSlot"))))
                                        {
                                            continue;
                                        }
                                        cProp.WriteTo(writer);
                                    }
                                    if (isTarget)
                                    {
                                        writer.WriteString("dischargeStatus", status);
                                        writer.WriteString("dischargedAt", dischargedAt.ToString("O"));
                                        writer.WriteString("condition", condition);
                                        writer.WriteString("inspectionNotes", notes);
                                        if (!string.IsNullOrEmpty(slot))
                                        {
                                            writer.WriteString("reservedSlot", slot);
                                        }
                                        if (damagePhotos != null && damagePhotos.Count > 0)
                                        {
                                            writer.WriteStartArray("damagePhotos");
                                            foreach (var photo in damagePhotos)
                                            {
                                                writer.WriteStringValue(photo);
                                            }
                                            writer.WriteEndArray();
                                        }
                                    }
                                    writer.WriteEndObject();
                                }
                                writer.WriteEndArray();
                            }
                            else
                            {
                                bProp.WriteTo(writer);
                            }
                        }
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
                else
                {
                    prop.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }

        var updatedJson = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        await _context.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var conn = _context.Database.GetDbConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE shipping_manifests SET payload = @payload::jsonb, revision = revision + 1 WHERE id = @id";
            var pJson = cmd.CreateParameter();
            pJson.ParameterName = "@payload";
            pJson.Value = updatedJson;
            cmd.Parameters.Add(pJson);

            var pId = cmd.CreateParameter();
            pId.ParameterName = "@id";
            pId.Value = manifestId;
            cmd.Parameters.Add(pId);

            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            await _context.Database.CloseConnectionAsync();
        }
    }

    private async Task MarkSlotOccupiedAsync(string slotCoordinate, string containerNo, CancellationToken cancellationToken)
    {
        var parts = slotCoordinate.Split(new[] { '-', '.' });
        if (parts.Length < 4) return;
        var blockCode = parts[0];
        if (!int.TryParse(parts[1], out var bay) || !int.TryParse(parts[2], out var row) || !int.TryParse(parts[3], out var tier))
            return;

        await _context.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var conn = _context.Database.GetDbConnection();

            // 1. Tìm hoặc tạo Container trong bảng containers
            Guid contId;
            using (var findCmd = conn.CreateCommand())
            {
                findCmd.CommandText = "SELECT id FROM containers WHERE container_no = @containerNo LIMIT 1";
                var p = findCmd.CreateParameter(); p.ParameterName = "@containerNo"; p.Value = containerNo; findCmd.Parameters.Add(p);
                var result = await findCmd.ExecuteScalarAsync(cancellationToken);
                if (result != null && result != DBNull.Value)
                {
                    contId = (Guid)result;
                    using var updateCmd = conn.CreateCommand();
                    updateCmd.CommandText = "UPDATE containers SET status = 'stored'::container_status, updated_at = now() WHERE id = @id";
                    var pId = updateCmd.CreateParameter(); pId.ParameterName = "@id"; pId.Value = contId; updateCmd.Parameters.Add(pId);
                    await updateCmd.ExecuteNonQueryAsync(cancellationToken);
                }
                else
                {
                    contId = Guid.NewGuid();
                    using var insertCmd = conn.CreateCommand();
                    insertCmd.CommandText = "INSERT INTO containers (id, container_no, status, created_at) VALUES (@id, @containerNo, 'stored'::container_status, now())";
                    var pId = insertCmd.CreateParameter(); pId.ParameterName = "@id"; pId.Value = contId; insertCmd.Parameters.Add(pId);
                    var pNo = insertCmd.CreateParameter(); pNo.ParameterName = "@containerNo"; pNo.Value = containerNo; insertCmd.Parameters.Add(pNo);
                    await insertCmd.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            // 2. Tìm Slot ID theo tọa độ
            Guid? slotId = null;
            using (var slotCmd = conn.CreateCommand())
            {
                slotCmd.CommandText = @"
                    SELECT s.id 
                    FROM yard_slots s 
                    JOIN yard_blocks b ON s.block_id = b.id 
                    WHERE b.code = @blockCode AND s.bay = @bay AND s.row_no = @row AND s.tier = @tier 
                    LIMIT 1";
                var pB = slotCmd.CreateParameter(); pB.ParameterName = "@blockCode"; pB.Value = blockCode; slotCmd.Parameters.Add(pB);
                var pBay = slotCmd.CreateParameter(); pBay.ParameterName = "@bay"; pBay.Value = bay; slotCmd.Parameters.Add(pBay);
                var pRow = slotCmd.CreateParameter(); pRow.ParameterName = "@row"; pRow.Value = row; slotCmd.Parameters.Add(pRow);
                var pTier = slotCmd.CreateParameter(); pTier.ParameterName = "@tier"; pTier.Value = tier; slotCmd.Parameters.Add(pTier);
                var res = await slotCmd.ExecuteScalarAsync(cancellationToken);
                if (res != null && res != DBNull.Value)
                {
                    slotId = (Guid)res;
                }
            }

            // 3. Cập nhật Slot sang occupied và ghi nhận container_positions
            if (slotId.HasValue)
            {
                using var assignCmd = conn.CreateCommand();
                assignCmd.CommandText = @"
                    UPDATE yard_slots SET status = 'occupied' WHERE id = @slotId;
                    UPDATE container_positions SET is_current = false WHERE slot_id = @slotId;
                    INSERT INTO container_positions (id, container_id, slot_id, placed_at, is_current)
                    VALUES (@newPosId, @contId, @slotId, now(), true);";
                var pSlot = assignCmd.CreateParameter(); pSlot.ParameterName = "@slotId"; pSlot.Value = slotId.Value; assignCmd.Parameters.Add(pSlot);
                var pCont = assignCmd.CreateParameter(); pCont.ParameterName = "@contId"; pCont.Value = contId; assignCmd.Parameters.Add(pCont);
                var pPos = assignCmd.CreateParameter(); pPos.ParameterName = "@newPosId"; pPos.Value = Guid.NewGuid(); assignCmd.Parameters.Add(pPos);
                await assignCmd.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        finally
        {
            await _context.Database.CloseConnectionAsync();
        }
    }

    private async Task SyncYardTasksForDischargePlanAsync(
        Guid manifestId,
        string vesselName,
        string voyageNumber,
        string berthCode,
        List<DischargeContainerItemDto> containers,
        CancellationToken cancellationToken)
    {
        try
        {
            var contNos = containers.Select(c => c.ContainerNumber).ToList();
            var existingTasks = await _context.Set<YardTask>()
                .Where(t => contNos.Contains(t.ContainerNo))
                .ToListAsync(cancellationToken);

            foreach (var cont in containers.Where(c => !string.IsNullOrEmpty(c.ReservedSlot)))
            {
                var task = existingTasks.FirstOrDefault(t => t.ContainerNo.Equals(cont.ContainerNumber, StringComparison.OrdinalIgnoreCase));
                var blockCode = cont.ReservedSlot?.Split('-')[0] ?? "R";
                var suffix = cont.ContainerNumber.Length >= 4 ? cont.ContainerNumber.Substring(cont.ContainerNumber.Length - 4) : cont.ContainerNumber;

                if (task == null)
                {
                    var newTask = new YardTask
                    {
                        TaskCode = $"TSK-DIS-{suffix}",
                        OperationType = "Dỡ Tàu Nhập",
                        ContainerNo = cont.ContainerNumber,
                        ContainerType = cont.IsoType ?? (cont.IsReefer ? "40RF" : "40HC"),
                        CargoType = cont.IsReefer ? "Hàng Lạnh (Reefer)" : "Hàng Khô Nhập Khẩu",
                        FromLocation = $"Cầu Tàu {berthCode} ({vesselName})",
                        ToLocation = cont.ReservedSlot,
                        BlockCode = blockCode,
                        VehiclePlate = "ITV-08",
                        DriverName = "Nguyễn Văn Tuấn (Đội xe Cảng)",
                        EquipmentCode = "RTG-02",
                        OperatorName = "Võ Hoàng Nam",
                        Priority = cont.IsReefer ? "Critical" : "High",
                        Status = cont.DischargeStatus == "IN_YARD" ? "Completed" : (cont.DischargeStatus == "DISCHARGED" ? "Ready" : "Assigned"),
                        AssignedBy = "Thuật Toán Phân Bổ Bãi",
                        AssignedAt = DateTime.UtcNow,
                        ExpectedSealNo = cont.SealNumber,
                        Notes = $"Hàng nhập tàu {vesselName} ({voyageNumber}). Vị trí bãi: {cont.ReservedSlot}. Điểm AI: {cont.AllocationScore}đ. {cont.AllocationReason}"
                    };
                    await _context.Set<YardTask>().AddAsync(newTask, cancellationToken);
                }
                else
                {
                    task.ToLocation = cont.ReservedSlot;
                    task.BlockCode = blockCode;
                    task.Notes = $"Hàng nhập tàu {vesselName} ({voyageNumber}). Vị trí bãi: {cont.ReservedSlot}. Điểm AI: {cont.AllocationScore}đ. {cont.AllocationReason}";
                    if (cont.DischargeStatus == "IN_YARD")
                    {
                        task.Status = "Completed";
                        task.CompletedLocation = cont.ReservedSlot;
                    }
                    else if (cont.DischargeStatus == "DISCHARGED" && task.Status == "Assigned")
                    {
                        task.Status = "Ready";
                    }
                }
            }
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SyncYardTasksForDischargePlan] Không thể đồng bộ yard_task");
        }
    }

    private async Task UpdateYardTaskOnTallyScanAsync(string containerNo, string slotCoordinate, bool confirmPutdown, string? condition, CancellationToken cancellationToken)
    {
        try
        {
            var task = await _context.Set<YardTask>().FirstOrDefaultAsync(t => t.ContainerNo == containerNo, cancellationToken);
            if (task != null)
            {
                if (string.IsNullOrEmpty(task.VehiclePlate))
                {
                    task.VehiclePlate = "ITV-08";
                    task.DriverName = "Nguyễn Văn Tuấn (Đội xe Cảng)";
                    task.EquipmentCode = "RTG-02";
                    task.OperatorName = "Võ Hoàng Nam";
                }

                if (confirmPutdown)
                {
                    task.Status = "Completed";
                    task.CompletedLocation = slotCoordinate;
                    task.EndTime = DateTime.UtcNow;
                    task.Notes = $"Đã hạ bãi an toàn tại ô {slotCoordinate}. {task.Notes}";
                }
                else
                {
                    task.Status = "Ready";
                    task.StartTime = DateTime.UtcNow;
                    task.Condition = condition ?? "Good";
                    task.Notes = $"Đã dỡ cầu cảng lúc {DateTime.UtcNow:HH:mm}. {task.Notes}";
                }
                await _context.SaveChangesAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[UpdateYardTaskOnTallyScan] Lỗi cập nhật yard_task cho container {ContNo}", containerNo);
        }
    }

    #endregion
}

internal static class ContainerSortExtensions
{
    public static void sortContainers(this List<DischargeContainerItemDto> list, string direction = "IMPORT")
    {
        list.Sort((a, b) =>
        {
            // 1. Cont lạnh (Reefer) luôn ưu tiên cao nhất để nhận slot có ổ cắm điện
            if (a.IsReefer && !b.IsReefer) return -1;
            if (!a.IsReefer && b.IsReefer) return 1;

            if (direction == "EXPORT")
            {
                // Quy tắc Hàng xuất (Mục 4.5.1):
                // Phân tách theo Cảng dỡ (POD) -> Hãng tàu -> Cỡ loại (20ft vs 40ft) -> Nhóm trọng lượng (Nặng trước)
                var podComp = string.Compare(a.Pod ?? "", b.Pod ?? "", StringComparison.OrdinalIgnoreCase);
                if (podComp != 0) return podComp;

                var lineComp = string.Compare(a.ShippingLine ?? "", b.ShippingLine ?? "", StringComparison.OrdinalIgnoreCase);
                if (lineComp != 0) return lineComp;

                var isA20 = (a.IsoType ?? "").StartsWith("20");
                var isB20 = (b.IsoType ?? "").StartsWith("20");
                if (isA20 != isB20) return isA20 ? -1 : 1;

                // Nặng xếp trước (tầng 1) -> Nhẹ xếp sau (tầng trên)
                return b.GrossWeightKg.CompareTo(a.GrossWeightKg);
            }
            else
            {
                // Quy tắc Hàng nhập (Mục 4.5.1):
                // Hãng tàu -> Cỡ loại -> Vận đơn B/L (FCL) hoặc Chủ khai thác (LCL) -> Trọng lượng nặng trước
                var lineComp = string.Compare(a.ShippingLine ?? "", b.ShippingLine ?? "", StringComparison.OrdinalIgnoreCase);
                if (lineComp != 0) return lineComp;

                var isA20 = (a.IsoType ?? "").StartsWith("20");
                var isB20 = (b.IsoType ?? "").StartsWith("20");
                if (isA20 != isB20) return isA20 ? -1 : 1;

                if (a.CargoType == "LCL" && b.CargoType == "LCL")
                {
                    var cComp = string.Compare(a.Consolidator ?? "", b.Consolidator ?? "", StringComparison.OrdinalIgnoreCase);
                    if (cComp != 0) return cComp;
                }
                else
                {
                    var blComp = string.Compare(a.BlNumber ?? "", b.BlNumber ?? "", StringComparison.OrdinalIgnoreCase);
                    if (blComp != 0) return blComp;
                }

                return b.GrossWeightKg.CompareTo(a.GrossWeightKg);
            }
        });
    }
}
