using Microsoft.EntityFrameworkCore;
using NexusPort.Modules.Booking.Application.DTOs;
using NexusPort.Shared.Exceptions;

namespace NexusPort.Modules.Booking.Application.Services;

public partial class BookingService
{
    private async Task<ExportGateMetadata?> ExportMetadataAsync(Guid carrier,Guid? exportBookingId,Guid? exportContainerId,Guid? gateBookingId,CancellationToken ct)
    {
        if(!exportBookingId.HasValue&&!exportContainerId.HasValue)return null;
        if(!exportBookingId.HasValue||!exportContainerId.HasValue)throw new ValidationException("ExportBookingId","Thiếu liên kết Booking hãng tàu/container.");
        var rows=await _context.Database.SqlQueryRaw<ExportGateMetadata>("""
            SELECT b.id AS "BookingId",e.id AS "ExportContainerId",e.container_id AS "ContainerId",e.declaration_id AS "DeclarationId",
              b.booking_no AS "BookingNumber",e.seal_number AS "SealNumber",e.vgm_kg AS "VgmKg",e.target_temperature AS "TargetTemperature",
              e.un_number AS "UnNumber",e.dg_class AS "DgClass",v.receive_from AS "ReceiveFrom",v.cy_cutoff AS "CyCutoff"
            FROM export_bookings b JOIN export_booking_containers e ON e.export_booking_id=b.id JOIN export_voyages v ON v.id=b.voyage_id
            WHERE b.id={0} AND e.id={1} AND b.carrier_id={2} AND b.port_status='Accepted'
              AND b.status IN ('ContainersDeclared','GateIn') AND v.status='Scheduled' AND e.status='Expected' AND e.active
            """,exportBookingId.Value,exportContainerId.Value,carrier).ToListAsync(ct);
        if(rows.Count!=1)throw new ValidationException("ExportBookingId","Container chưa được hãng tàu/cảng tiếp nhận, không thuộc công ty hoặc chuyến đã đóng.");
        var row=rows[0];
        var occupied=await _context.Database.SqlQueryRaw<int>("""
            SELECT count(*)::int AS "Value" FROM export_gate_links l JOIN bookings b ON b.id=l.gate_booking_id
            WHERE l.export_container_id={0} AND b.status::text NOT IN ('canceled','rejected','expired') AND ({1}::uuid IS NULL OR b.id<>{1})
            """,row.ExportContainerId,(object?)gateBookingId??DBNull.Value).SingleAsync(ct);
        if(occupied>0)throw new ValidationException("ExportContainerId","Container đã có lịch xe đang hiệu lực. Xem hoặc điều chỉnh lịch đã tạo.");
        return row;
    }
    private static void ExportWindow(ExportGateMetadata row,DateTime start,DateTime end)
    {
        if(start.ToUniversalTime()<row.ReceiveFrom.ToUniversalTime()||end.ToUniversalTime()>row.CyCutoff.ToUniversalTime())
            throw new ValidationException("Appointment","Giờ hẹn phải nằm trong thời gian cảng mở nhận container và kết thúc trước CY cut-off của chuyến tàu.");
    }
    private async Task ValidateExportLinkAsync(CreateBookingDto dto,CancellationToken ct)
    {
        var row=await ExportMetadataAsync(dto.CarrierId,dto.ExportBookingId,dto.ExportContainerId,dto.ExcludeBookingId,ct);
        if(row==null)return;
        if(_context.Database.CurrentTransaction!=null)
        {
            await _context.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM export_bookings WHERE id={row.BookingId} FOR UPDATE",ct);
            // A concurrent request may have assigned or cancelled this container while we waited.
            row=await ExportMetadataAsync(dto.CarrierId,dto.ExportBookingId,dto.ExportContainerId,dto.ExcludeBookingId,ct)
                ??throw new ValidationException("ExportBookingId","Liên kết xuất khẩu không còn hợp lệ.");
        }
        var selection=dto.ContainerSelections.SingleOrDefault(x=>x.ContainerId==row.ContainerId&&x.DeclarationId==row.DeclarationId);
        var operation=dto.Operations.SingleOrDefault(x=>x.OperationType=="Export"&&x.ContainerId==row.ContainerId);
        if(selection==null||operation==null)throw new ValidationException("ExportContainerId","Lịch Giao phải dùng đúng container và hồ sơ nguồn đã được hãng tàu tiếp nhận.");
        ExportWindow(row,dto.AppointmentStart,dto.AppointmentEnd);
        operation.CarrierBookingNumber=row.BookingNumber;operation.SealNumber=row.SealNumber;operation.GrossWeightKg=row.VgmKg;
        operation.TemperatureSetpoint=row.TargetTemperature;operation.UnNumber=row.UnNumber;operation.DgClass=row.DgClass;
        dto.ExportLinkValidated=true;
    }
    private async Task ApplyExportPlanningAsync(Guid carrier,BookingPlanningRequest request,BookingCargoSource source,CancellationToken ct)
    {
        if(request.BookingId.HasValue&&!request.ExportBookingId.HasValue&&!request.ExportContainerId.HasValue)
        {
            var link=await ExistingExportLinkAsync(request.BookingId.Value,ct);
            request.ExportBookingId=link?.BookingId;request.ExportContainerId=link?.ExportContainerId;
        }
        var row=await ExportMetadataAsync(carrier,request.ExportBookingId,request.ExportContainerId,request.BookingId,ct);
        if(row==null)return;
        if(source.ContainerId!=row.ContainerId||source.DeclarationId!=row.DeclarationId||request.BookingType!="Dropoff")
            throw new ValidationException("ExportContainerId","Container/hồ sơ không khớp Booking xuất khẩu.");
        ExportWindow(row,request.AppointmentStart,request.AppointmentEnd);
        source.SealNumber=row.SealNumber;source.GrossWeightKg=row.VgmKg;
    }
    private async Task<ExportGateMetadata?> ExistingExportLinkAsync(Guid bookingId,CancellationToken ct)
    {
        if(!await _context.Database.SqlQueryRaw<bool>("SELECT to_regclass('public.export_gate_links') IS NOT NULL AS \"Value\"").SingleAsync(ct))return null;
        return await _context.Database.SqlQueryRaw<ExportGateMetadata>("""
            SELECT l.export_booking_id AS "BookingId",l.export_container_id AS "ExportContainerId",
              e.container_id AS "ContainerId",e.declaration_id AS "DeclarationId",b.booking_no AS "BookingNumber",
              e.seal_number AS "SealNumber",e.vgm_kg AS "VgmKg",e.target_temperature AS "TargetTemperature",
              e.un_number AS "UnNumber",e.dg_class AS "DgClass",v.receive_from AS "ReceiveFrom",v.cy_cutoff AS "CyCutoff"
            FROM export_gate_links l JOIN export_booking_containers e ON e.id=l.export_container_id
            JOIN export_bookings b ON b.id=l.export_booking_id JOIN export_voyages v ON v.id=b.voyage_id
            WHERE l.gate_booking_id={0}
            """,bookingId).SingleOrDefaultAsync(ct);
    }
}
public sealed class ExportGateMetadata
{
    public Guid BookingId { get; set; }
    public Guid ExportContainerId { get; set; }
    public Guid ContainerId { get; set; }
    public Guid DeclarationId { get; set; }
    public string BookingNumber { get; set; }="";
    public string SealNumber { get; set; }="";
    public decimal VgmKg { get; set; }
    public decimal? TargetTemperature { get; set; }
    public string? UnNumber { get; set; }
    public string? DgClass { get; set; }
    public DateTime ReceiveFrom { get; set; }
    public DateTime CyCutoff { get; set; }
}
