using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using NexusPort.Modules.Booking.Application.DTOs;
using NexusPort.Modules.Booking.Domain.Enums;

namespace NexusPort.Modules.Booking.Application.Services;

public sealed partial class ExportBookingService
{
    private static string PolicyCargo(string? cargo) => cargo switch
    {
        "reefer" => "Reefer", "dangerous" or "dangerous_goods" => "DangerousGoods",
        "oversized" => "Oversized", "perishable" => "Perishable", "overweight" => "Overweight", _ => "General"
    };
    private static string Plate(string? value) => new((value ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static bool Overlap(ExportSubmissionTrip a, ExportSubmissionTrip b) => a.AppointmentStart < b.AppointmentEnd && a.AppointmentEnd > b.AppointmentStart;
    private static (DateTime Start, DateTime End) NextSubmissionWindow(ExportSubmissionTrip trip)
    {
        var duration = trip.AppointmentEnd - trip.AppointmentStart;
        var start = trip.AppointmentStart.AddHours(1);
        // Appointment inputs use one Vietnam calendar day. Keep automatic suggestions editable there.
        if (start.AddHours(7).Date != start.Add(duration).AddHours(7).Date)
            start = DateTime.SpecifyKind(start.AddHours(7).Date.AddDays(1).AddHours(-7), DateTimeKind.Utc);
        return (start, start.Add(duration));
    }

    private async Task<JsonObject> SubmissionVoyage(ExportSubmissionRequest request, CancellationToken ct)
    {
        Require(request.Trips is { Count: >= 1 and <= 50 } && request.Trips.All(t => t != null), "Trips", "Chọn từ 1 đến 50 container.");
        Require(request.Trips.Select(t => t.ContainerId).Distinct().Count() == request.Trips.Count &&
            request.Trips.Select(t => t.DeclarationId).Distinct().Count() == request.Trips.Count, "Containers", "Mỗi container và bản khai chỉ được chọn một lần.");
        Require(request.Pickup == null || (request.Trips.Count == 1 && request.Pickup.ContainerId != Guid.Empty &&
            request.Pickup.ContainerId != request.Trips[0].ContainerId && !string.IsNullOrWhiteSpace(request.Pickup.YardCode)),
            "Pickup", "Chuyến hai chiều cần đúng một container Giao và một container Nhận khác nhau, có mã nhận hợp lệ.");
        Require(request.Description == null || request.Description.Length <= 1000, "Description", "Ghi chú tối đa 1000 ký tự.");
        var voyage = await Voyage(request.VoyageId, false, ct);
        Require(G(voyage, "shippingLineId") == request.ShippingLineId && S(voyage, "status") == "Scheduled" &&
            D(voyage, "vgmCutoff") > DateTime.UtcNow, "VoyageId", "Chọn chuyến còn mở của đúng hãng tàu.");
        Require((await Rows("SELECT jsonb_build_object('id',id)::text AS \"Value\" FROM users WHERE id=@p0 AND role='Shipping Line' AND is_active", ct, request.ShippingLineId)).Count == 1,
            "ShippingLineId", "Chọn hãng tàu đang hoạt động.");
        foreach (var trip in request.Trips)
        {
            BookingFleetPolicy.ValidateWindow(trip.AppointmentStart, trip.AppointmentEnd);
            Require(trip.AppointmentStart.Kind == DateTimeKind.Utc && trip.AppointmentEnd.Kind == DateTimeKind.Utc &&
                trip.AppointmentStart >= DateTime.UtcNow.AddMinutes(-1) && trip.AppointmentStart >= D(voyage, "receiveFrom") &&
                trip.AppointmentEnd <= D(voyage, "cyCutoff"), "Appointment", "Lịch đề xuất phải ở tương lai, trong thời gian mở nhận và kết thúc trước CY cut-off.");
        }
        return voyage;
    }

    private async Task CheckSubmissionSources(ExportSubmissionRequest request, Guid carrier, CancellationToken ct)
    {
        if (request.BookingId.HasValue)
        {
            var existing = await Booking(request.BookingId.Value, false, ct); await Own(existing, ct);
            Require(S(existing, "status") is "Pending" or "Rejected" && existing["submission"] != null,
                "Status", "Chỉ sửa hồ sơ chưa được hãng tàu duyệt.");
        }
        foreach (var trip in request.Trips)
        {
            var unavailable = await Rows("""
                SELECT jsonb_build_object('count',count(*)::int)::text AS "Value" FROM export_booking_containers e
                WHERE e.container_id=@p0 AND e.active AND (@p1::uuid IS NULL OR e.export_booking_id<>@p1)
                """, ct, trip.ContainerId, request.BookingId);
            Require(I(unavailable[0], "count") == 0, "Container", "Container đã được gửi trong một Booking hãng tàu khác còn hiệu lực.");
        }
    }

    private static BookingBatchRequest SubmissionBatch(ExportSubmissionRequest request, Guid carrier) => new()
    {
        RequestId = request.RequestId, CarrierId = carrier, BookingType = BookingType.Dropoff,
        Description = request.Description, SharedDocumentsConfirmed = request.SharedDocumentsConfirmed,
        SharedDocuments = request.SharedDocuments,
        Trips = request.Trips.Select(trip => new BookingBatchTripRequest
        {
            Selection = new() { ContainerId = trip.ContainerId, DeclarationId = trip.DeclarationId },
            AppointmentStart = trip.AppointmentStart, AppointmentEnd = trip.AppointmentEnd,
            TruckId = trip.TruckId, DriverId = trip.DriverId, TrailerNumber = trip.TrailerNumber,
            IsReeferPowerRequired = trip.IsReeferPowerRequired,
            Operation = new() { ContainerId = trip.ContainerId, TemperatureSetpoint = trip.TargetTemperature, UnNumber = trip.UnNumber,
                DgClass = trip.DgClass, Documents = trip.Documents, TemperatureUnit = "C" }
        }).ToList()
    };

    public async Task<BookingBatchPlan> PlanSubmission(ExportSubmissionRequest request, CancellationToken ct)
    {
        var carrier = await Company(ct);
        var voyage = await SubmissionVoyage(request, ct); await CheckSubmissionSources(request, carrier, ct);
        var plan = await PlanSubmissionFleet(request, carrier, ct);
        Require(plan.ValidationMessages.Count == 0, "Containers", string.Join(" ", plan.ValidationMessages));
        Require(plan.Trips.Select(t => t.Plan.Source.CargoType).Distinct().Count() == 1, "CargoType", "Các container trong lô phải cùng loại hàng.");
        // Preserve successful draft assignments; stagger only trips that cannot use the current window.
        // The normal batch planner reserves resources between overlapping draft trips on each pass.
        for (var attempt = 0; request.OptimizeSchedule && attempt < 24; attempt++)
        {
            var unavailable = plan.Trips.Select((trip, index) => new { Trip = trip, Index = index })
                .Where(row => !row.Trip.Plan.Schedule.IsAvailable).ToList();
            if (unavailable.Count == 0 || unavailable.Any(row => row.Trip.Plan.Schedule.RequiresExistingBookingResolution)) break;
            if (unavailable.Any(row => NextSubmissionWindow(request.Trips[row.Index]).End > D(voyage, "cyCutoff"))) break;
            foreach (var row in unavailable)
            {
                var window = NextSubmissionWindow(request.Trips[row.Index]);
                request.Trips[row.Index].AppointmentStart = window.Start;
                request.Trips[row.Index].AppointmentEnd = window.End;
            }
            plan = await PlanSubmissionFleet(request, carrier, ct);
        }
        for (var index = 0; index < plan.Trips.Count; index++)
        {
            plan.Trips[index].AppointmentStart = request.Trips[index].AppointmentStart;
            plan.Trips[index].AppointmentEnd = request.Trips[index].AppointmentEnd;
        }
        return plan;
    }

    private async Task<BookingBatchPlan> PlanSubmissionFleet(ExportSubmissionRequest request, Guid carrier, CancellationToken ct)
    {
        var plan = await _bookings.PlanBatchAsync(SubmissionBatch(request, carrier), ct, allowSingle: true);
        if (request.Pickup == null) return plan;
        var trip = request.Trips.Single();
        var combined = await _bookings.PlanBookingAsync(carrier, new BookingPlanningRequest
        {
            AppointmentStart = trip.AppointmentStart, AppointmentEnd = trip.AppointmentEnd,
            Legs = new()
            {
                new() { ContainerId = trip.ContainerId, DeclarationId = trip.DeclarationId, BookingType = "Dropoff" },
                new() { ContainerId = request.Pickup.ContainerId, YardCode = request.Pickup.YardCode, BookingType = "Pickup" }
            }
        }, ct);
        var result = plan.Trips.Single();
        result.Plan = combined;
        result.CargoAvailable = combined.Schedule.IsAvailable;
        result.TruckId = combined.Fleet.RecommendedTruckId;
        result.DriverId = combined.Fleet.RecommendedDriverId;
        result.TrailerNumber = result.TrailerSuggestions.FirstOrDefault(t => t.TruckId == result.TruckId)?.TrailerNumber;
        return plan;
    }

    private static void CheckOriginalDocument(CreateBookingDocumentDto? doc)
    {
        if (doc is null) throw new NexusPort.Shared.Exceptions.ValidationException("Documents", "Chứng từ không được để trống.");
        Require(!string.IsNullOrWhiteSpace(doc.DocumentType) && doc.DocumentType.Length <= 100 &&
            !string.IsNullOrWhiteSpace(doc.FileUrl) && doc.FileUrl.Length <= 4000 &&
            ((Uri.TryCreate(doc.FileUrl, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http") ||
            doc.FileUrl.StartsWith("/uploads/documents/", StringComparison.Ordinal)), "Documents", "Chứng từ cần tệp gốc và đường dẫn hợp lệ.");
        Require(!doc.ExpiryDate.HasValue || doc.ExpiryDate.Value.Date >= DateTime.UtcNow.Date, "Documents", "Có chứng từ đã hết hạn.");
        doc.BookingOperationId = null;
    }

    private async Task CheckSubmissionDocuments(ExportSubmissionRequest request, BookingBatchPlan plan, CancellationToken ct)
    {
        Require(request.SharedDocuments is { Count: <= 30 }, "Documents", "Tối đa 30 chứng từ dùng chung.");
        Require(request.SharedDocuments.Count == 0 || request.Trips.Count == 1 || request.SharedDocumentsConfirmed,
            "SharedDocuments", "Xác nhận chứng từ dùng chung thực sự bao gồm các container đã chọn.");
        var numbers = plan.Trips.Select(t => t.Plan.Source.ContainerNumber.ToUpperInvariant()).ToHashSet();
        foreach (var shared in request.SharedDocuments)
            Require(shared != null && shared.ContainerNumbers != null && shared.ContainerNumbers.Count <= 500 &&
                shared.ContainerNumbers.Distinct(StringComparer.OrdinalIgnoreCase).Count() == shared.ContainerNumbers.Count &&
                numbers.IsSubsetOf(shared.ContainerNumbers.Select(n => (n ?? "").Trim().ToUpperInvariant())),
                "SharedDocuments", "Phạm vi của từng chứng từ dùng chung phải bao gồm đầy đủ container trong lô.");
        for (var index = 0; index < request.Trips.Count; index++)
        {
            var trip = request.Trips[index]; var source = plan.Trips[index].Plan.Source;
            Require(ValidIso6346(source.ContainerNumber), "Container", $"{source.ContainerNumber}: số container không đúng ISO 6346/check digit.");
            Text(trip.SealNumber, "Seal", 100);
            Require(trip.VgmKg > 0 && trip.VgmKg <= 99999999, "VGM", $"{source.ContainerNumber}: nhập VGM thực tế đã xác minh, tính bằng kg.");
            Require(source.CargoType != "reefer" || trip.TargetTemperature.HasValue, "Temperature", "Bổ sung nhiệt độ chỉ dẫn cho container lạnh.");
            trip.IsReeferPowerRequired = trip.IsReeferPowerRequired && source.CargoType == "reefer";
            Require(!trip.IsReeferPowerRequired || source.RequiredPowerKw > 0, "RequiredPowerKw", "Bổ sung công suất cấp điện trong bản khai container trước khi yêu cầu cấp điện tại bãi.");
            Require(!new[] { "dangerous", "dangerous_goods" }.Contains(source.CargoType) ||
                (!string.IsNullOrWhiteSpace(trip.UnNumber) && !string.IsNullOrWhiteSpace(trip.DgClass)), "DangerousGoods", "Bổ sung mã UN và phân lớp hàng nguy hiểm.");
            Require(trip.UnNumber?.Length <= 30 || trip.UnNumber == null, "UN", "Mã UN tối đa 30 ký tự.");
            Require(trip.DgClass?.Length <= 30 || trip.DgClass == null, "DgClass", "Phân lớp tối đa 30 ký tự.");
            Require(trip.Documents is { Count: <= 30 }, "Documents", "Tối đa 30 chứng từ cho mỗi container.");
            var documents = request.SharedDocuments.Cast<CreateBookingDocumentDto>().Concat(trip.Documents).ToList();
            foreach (var doc in documents)
            {
                CheckOriginalDocument(doc);
            }
            foreach (var spec in _documents.GetRequiredDocuments("Export", "Loaded", PolicyCargo(source.CargoType))
                .Where(s => s.IsMandatory && s.DocumentType is not ("CarrierBookingReference" or "VgmCertificate")))
                Require(documents.Any(d => d.DocumentType == spec.DocumentType), "Documents", $"{source.ContainerNumber}: thiếu {spec.NameVi}.");
            var customs = documents.FirstOrDefault(d => d.DocumentType == "CustomsDeclaration");
            // A stored URL is cargo evidence, never an automatic customs-release decision.
            trip.CustomsDocumentUrl = customs?.FileUrl;
        }
        if (request.Pickup is { } pickup)
        {
            var source = plan.Trips.Single().Plan.Sources.Single(s => s.ContainerId == pickup.ContainerId);
            Require(pickup.Documents is { Count: <= 30 }, "PickupDocuments", "Tối đa 30 chứng từ cho container Nhận.");
            foreach (var doc in pickup.Documents) CheckOriginalDocument(doc);
            foreach (var spec in _documents.GetRequiredDocuments("Import", "Loaded", PolicyCargo(source.CargoType)).Where(s => s.IsMandatory))
                Require(pickup.Documents.Any(d => d.DocumentType == spec.DocumentType), "PickupDocuments", $"{source.ContainerNumber}: thiếu {spec.NameVi}.");
            Require(source.CargoType != "reefer" || pickup.TargetTemperature.HasValue, "PickupTemperature", "Bổ sung chỉ dẫn nhiệt độ cho container Nhận hàng lạnh.");
            Require(!new[] { "dangerous", "dangerous_goods" }.Contains(source.CargoType) ||
                (!string.IsNullOrWhiteSpace(pickup.UnNumber) && !string.IsNullOrWhiteSpace(pickup.DgClass)), "PickupDangerousGoods", "Bổ sung mã UN và phân lớp của container Nhận hàng nguy hiểm.");
            Require(pickup.UnNumber?.Length <= 30 || pickup.UnNumber == null, "PickupUN", "Mã UN tối đa 30 ký tự.");
            Require(pickup.DgClass?.Length <= 30 || pickup.DgClass == null, "PickupDgClass", "Phân lớp tối đa 30 ký tự.");
        }
        await Task.CompletedTask;
    }

    private async Task CheckSubmissionFleet(ExportSubmissionRequest request, BookingBatchPlan plan, bool reoptimize, CancellationToken ct)
    {
        for (var index = 0; index < request.Trips.Count; index++)
        {
            var trip = request.Trips[index]; var proposed = plan.Trips[index]; var fleet = proposed.Plan.Fleet;
            Require(proposed.CargoAvailable, "Container", proposed.Plan.Schedule.Message);
            var other = request.Trips.Take(index).Where(t => Overlap(t, trip)).ToList();
            var freeTrucks = fleet.Trucks.Where(t => !other.Any(o => o.TruckId == t.Id)).ToList();
            var freeDrivers = fleet.Drivers.Where(d => !other.Any(o => o.DriverId == d.Id)).ToList();
            if (reoptimize)
            {
                var ranked = BookingFleetPolicy.Rank(freeTrucks, freeDrivers);
                if (!freeTrucks.Any(t => t.Id == trip.TruckId)) trip.TruckId = ranked.RecommendedTruckId;
                if (!freeDrivers.Any(d => d.Id == trip.DriverId)) trip.DriverId = ranked.RecommendedDriverId;
            }
            Require(trip.TruckId.HasValue && freeTrucks.Any(t => t.Id == trip.TruckId) &&
                trip.DriverId.HasValue && freeDrivers.Any(d => d.Id == trip.DriverId), "Fleet",
                $"{proposed.Plan.Source.ContainerNumber}: xe hoặc tài xế đã bận/không thuộc công ty. Tối ưu lại lịch trước khi gửi.");
            Text(trip.TrailerNumber ?? "", "Biển số rơ-moóc", 50);
            Require(!other.Any(o => Plate(o.TrailerNumber) == Plate(trip.TrailerNumber)), "Trailer", "Các chuyến trùng giờ phải sử dụng rơ-moóc khác nhau.");
            var occupied = await Rows("""
                SELECT jsonb_build_object('count',count(*)::int)::text AS "Value" FROM bookings b
                WHERE regexp_replace(upper(b.trailer_number),'[^A-Z0-9]','','g')=@p0
                  AND b.status::text NOT IN ('canceled','rejected','expired')
                  AND ((b.status::text<>'completed' AND b.appointment_start<@p2 AND b.appointment_end>@p1)
                    OR b.status::text='checked_in' OR b.trip_status IN ('Arrived','Processing','PartiallyCompleted')
                    OR EXISTS(SELECT 1 FROM gate_transactions g WHERE g.booking_id=b.id AND g.gate_type='gate_in' AND g.status='completed'
                      AND NOT EXISTS(SELECT 1 FROM gate_transactions x WHERE x.booking_id=b.id AND x.gate_type='gate_out' AND x.status='completed' AND x.checked_at>=g.checked_at)))
                """, ct, Plate(trip.TrailerNumber), trip.AppointmentStart, trip.AppointmentEnd);
            Require(I(occupied[0], "count") == 0, "Trailer", "Rơ-moóc đang có lịch trùng hoặc chuyến chưa kết thúc.");
        }
    }

    public async Task<JsonObject> Submit(ExportSubmissionRequest request, CancellationToken ct)
    {
        var carrier = await Company(ct);
        Require(request.RequestId != Guid.Empty, "RequestId", "Thiếu mã yêu cầu chống tạo trùng.");
        var fingerprint = Hash(request);
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await Exec("SELECT pg_advisory_xact_lock(hashtext(@p0))", ct, "export-submit:" + carrier + ":" + request.RequestId);
        var replay = await Rows("SELECT jsonb_build_object('id',id,'hash',request_hash)::text AS \"Value\" FROM export_bookings WHERE carrier_id=@p0 AND request_id=@p1", ct, carrier, request.RequestId);
        if (replay.Count > 0)
        {
            Require(S(replay[0], "hash") == fingerprint, "RequestId", "Mã yêu cầu đã dùng với nội dung khác.");
            await tx.CommitAsync(ct); return await Detail(G(replay[0], "id"), ct);
        }
        JsonObject? previous = null;
        if (request.BookingId.HasValue)
        {
            previous = await Booking(request.BookingId.Value, true, ct); await Own(previous, ct); Version(previous, request.ExpectedVersion);
            Require(S(previous, "status") is "Pending" or "Rejected" && previous["submission"] != null, "Status", "Chỉ sửa hồ sơ chưa được hãng tàu duyệt.");
        }
        await SubmissionVoyage(request, ct);
        foreach (var id in request.Trips.Select(t => t.ContainerId).Concat(request.Pickup == null ? Array.Empty<Guid>() : new[] { request.Pickup.ContainerId }).Distinct().OrderBy(x => x))
            await Exec("SELECT id FROM containers WHERE id=@p0 FOR UPDATE", ct, id);
        await CheckSubmissionSources(request, carrier, ct);
        var plan = await PlanSubmissionFleet(request, carrier, ct);
        Require(plan.ValidationMessages.Count == 0, "Containers", string.Join(" ", plan.ValidationMessages));
        Require(plan.Trips.Select(t => t.Plan.Source.CargoType).Distinct().Count() == 1, "CargoType", "Booking theo lô chỉ gồm container cùng loại hàng.");
        await CheckSubmissionDocuments(request, plan, ct); await CheckSubmissionFleet(request, plan, false, ct);
        var bookingId = previous == null ? Guid.NewGuid() : G(previous, "id");
        var types = plan.Trips.Select(t => t.Plan.Source.ContainerTypeCode).Distinct().ToList();
        var type = types.Count == 1 ? types[0] : "MIXED";
        var description = string.IsNullOrWhiteSpace(request.Description) ? PolicyCargo(plan.Trips[0].Plan.Source.CargoType) : request.Description.Trim();
        var snapshot = JsonSerializer.SerializeToNode(request, Json)!.AsObject();
        snapshot["plannedAt"] = DateTime.UtcNow;
        if (request.Pickup != null) snapshot["pickupSource"] = JsonSerializer.SerializeToNode(plan.Trips.Single().Plan.Sources.Single(s => s.ContainerId == request.Pickup.ContainerId), Json);
        snapshot["fleet"] = JsonSerializer.SerializeToNode(plan.Trips.Select((trip, i) => new
        {
            containerId = trip.ContainerId, vehicle = trip.Plan.Fleet.Trucks.First(t => t.Id == request.Trips[i].TruckId),
            driver = trip.Plan.Fleet.Drivers.First(d => d.Id == request.Trips[i].DriverId)
        }), Json);
        if (previous == null)
            await Exec("""
                INSERT INTO export_bookings(id,request_id,request_hash,carrier_id,line_user_id,voyage_id,booking_no,container_type_code,quantity,commodity_description,created_by,submission)
                VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10,@p11::jsonb)
                """, ct, bookingId, request.RequestId, fingerprint, carrier, request.ShippingLineId, request.VoyageId, "EXP-" + bookingId.ToString("N").ToUpperInvariant(), type, request.Trips.Count, description, Actor, snapshot.ToJsonString());
        else
        {
            await Exec("DELETE FROM export_booking_containers WHERE export_booking_id=@p0", ct, bookingId);
            await Exec("""
                UPDATE export_bookings SET request_id=@p0,request_hash=@p1,line_user_id=@p2,voyage_id=@p3,container_type_code=@p4,quantity=@p5,
                    commodity_description=@p6,submission=@p7::jsonb,status='Pending',port_status='Pending',version=version+1,updated_at=now() WHERE id=@p8
                """, ct, request.RequestId, fingerprint, request.ShippingLineId, request.VoyageId, type, request.Trips.Count, description, snapshot.ToJsonString(), bookingId);
        }
        for (var index = 0; index < request.Trips.Count; index++)
        {
            var trip = request.Trips[index]; var source = plan.Trips[index].Plan.Source;
            await Exec("""
                INSERT INTO export_booking_containers(id,export_booking_id,container_id,declaration_id,container_no,type_code,seal_number,vgm_kg,cargo_type,target_temperature,un_number,dg_class,customs_document_url)
                VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10,@p11,@p12)
                """, ct, Guid.NewGuid(), bookingId, trip.ContainerId, trip.DeclarationId, source.ContainerNumber, source.ContainerTypeCode,
                trip.SealNumber.Trim(), trip.VgmKg, source.CargoType, trip.TargetTemperature, trip.UnNumber, trip.DgClass, trip.CustomsDocumentUrl);
        }
        await History(bookingId, request.VoyageId, previous == null ? "BookingSubmitted" : "BookingResubmitted",
            "Khách hàng gửi hồ sơ container, chứng từ và lịch xe đề xuất cho hãng tàu; chưa tạo lịch cảng hoặc lượt bãi.", ct);
        await tx.CommitAsync(ct); return await Detail(bookingId, ct);
    }

    // Called only within the shipping-line decision transaction. Every journey remains Pending for port review.
    private async Task ForwardSubmission(JsonObject booking, CancellationToken ct)
    {
        var stored = booking["submission"]!.AsObject();
        var request = stored.Deserialize<ExportSubmissionRequest>(Json)!;
        var carrier = G(booking, "carrierId");
        await SubmissionVoyage(request, ct);
        var batch = SubmissionBatch(request, carrier);
        var records = await Containers(G(booking, "id"), ct);
        var plan = await PlanSubmissionFleet(request, carrier, ct);
        Require(plan.ValidationMessages.Count == 0, "Containers", string.Join(" ", plan.ValidationMessages));
        await CheckSubmissionFleet(request, plan, true, ct);
        batch = SubmissionBatch(request, carrier); batch.CreatedBy = G(booking, "createdBy");
        batch.SharedDocumentsConfirmed = request.SharedDocuments.Count == 0 || request.SharedDocumentsConfirmed;
        for (var index = 0; index < batch.Trips.Count; index++)
        {
            var trip = batch.Trips[index]; var source = request.Trips[index];
            var record = records.Single(r => G(r, "containerId") == source.ContainerId);
            trip.ExportBookingId = G(booking, "id"); trip.ExportContainerId = G(record, "id");
        }
        JsonNode delivery;
        if (batch.Trips.Count > 1)
        {
            var saved = await _bookings.CreateBatchAsync(batch, ct);
            delivery = JsonSerializer.SerializeToNode(new { batchId = saved.Id, batchCode = saved.BatchCode,
                trips = saved.Trips.Select(t => new { bookingId = t.Booking.Id, bookingCode = t.Booking.BookingCode,
                    t.Booking.AppointmentStart, t.Booking.AppointmentEnd, t.Booking.VehiclePlate, t.Booking.DriverName, t.Booking.TrailerNumber }) }, Json)!;
        }
        else
        {
            var trip = batch.Trips[0];
            trip.Operation.Documents = request.SharedDocuments.Cast<CreateBookingDocumentDto>().Concat(trip.Operation.Documents).ToList();
            var journey = new CreateBookingDto
            {
                CarrierId = carrier, ExportBookingId = trip.ExportBookingId, ExportContainerId = trip.ExportContainerId,
                BookingCode = "GATE-" + G(booking, "id").ToString("N").ToUpperInvariant(), BookingType = BookingType.Dropoff,
                ContainerSelections = new() { trip.Selection }, Operations = new() { trip.Operation },
                AppointmentStart = trip.AppointmentStart, AppointmentEnd = trip.AppointmentEnd,
                TruckId = trip.TruckId, DriverId = trip.DriverId, TrailerNumber = trip.TrailerNumber,
                IsReeferPowerRequired = trip.IsReeferPowerRequired, TargetTemperature = trip.Operation.TemperatureSetpoint,
                RequiredPowerKw = plan.Trips[0].Plan.Source.RequiredPowerKw, Description = request.Description
            };
            if (request.Pickup is { } pickup)
            {
                journey.IsCombined = true;
                journey.ContainerSelections.Add(new() { ContainerId = pickup.ContainerId, YardCode = pickup.YardCode });
                journey.Operations.Add(new()
                {
                    OperationType = "Import", ExpectedGateDirection = "GateOut", SequenceNumber = 2, ContainerId = pickup.ContainerId,
                    TemperatureSetpoint = pickup.TargetTemperature, UnNumber = pickup.UnNumber, DgClass = pickup.DgClass, Documents = pickup.Documents
                });
            }
            var saved = await _bookings.CreateAsync(journey, ct);
            delivery = JsonSerializer.SerializeToNode(new { trips = new[] { new { bookingId = saved.Id, bookingCode = saved.BookingCode,
                saved.AppointmentStart, saved.AppointmentEnd, saved.VehiclePlate, saved.DriverName, saved.TrailerNumber } } }, Json)!;
        }
        stored["delivery"] = delivery;
        await Exec("UPDATE export_bookings SET submission=@p0::jsonb WHERE id=@p1", ct, stored.ToJsonString(), G(booking, "id"));
    }
}
