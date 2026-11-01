using Microsoft.EntityFrameworkCore;
using NexusPort.Modules.Booking.Application.DTOs;
using NexusPort.Modules.Booking.Domain.Enums;
using NexusPort.Shared.Exceptions;

namespace NexusPort.Modules.Booking.Application.Services;

public partial class BookingService
{
    private async Task<decimal> PrepareBookingCargoAsync(CreateBookingDto dto,CancellationToken token)
    {
        if(!dto.IsCombined&&dto.Operations.Count<=1)return await PrepareSingleBookingCargoAsync(dto,token);
        if(dto.Operations.Count!=2||dto.ContainerSelections.Count!=2||dto.ContainerSelections.Select(selection=>selection.ContainerId).Distinct().Count()!=2||
            dto.Operations.Count(op=>op.OperationType=="Export")!=1||dto.Operations.Count(op=>op.OperationType=="Import")!=1||
            !dto.Operations.Select(op=>op.ContainerId).ToHashSet().SetEquals(dto.ContainerSelections.Select(selection=>(Guid?)selection.ContainerId)))
            throw new ValidationException("Operations","Chuyến hai chiều cần đúng một container Giao và một container Nhận khác nhau, được chọn từ danh sách.");
        foreach(var id in dto.ContainerSelections.Select(selection=>selection.ContainerId).OrderBy(id=>id))
            await _context.Database.SqlQueryRaw<Guid>("SELECT id AS \"Value\" FROM containers WHERE id={0} FOR UPDATE",id).ToListAsync(token);
        dto.Operations=dto.Operations.OrderBy(op=>op.OperationType=="Export"?0:1).ToList();
        var total=0m;
        for(var index=0;index<dto.Operations.Count;index++)
        {
            var op=dto.Operations[index];
            var leg=new CreateBookingDto {CarrierId=dto.CarrierId,BookingCode=dto.BookingCode,
                BookingType=index==0?BookingType.Dropoff:BookingType.Pickup,AppointmentStart=dto.AppointmentStart,AppointmentEnd=dto.AppointmentEnd,
                TruckId=dto.TruckId,DriverId=dto.DriverId,ContainerSelections=new(){dto.ContainerSelections.Single(selection=>selection.ContainerId==op.ContainerId)},
                Operations=new(){op},IsReeferPowerRequired=index==0&&dto.IsReeferPowerRequired,TempTolerance=dto.TempTolerance};
            total+=await PrepareSingleBookingCargoAsync(leg,token);
            op.SequenceNumber=index+1;
            if(index==0){dto.IsReeferPowerRequired=leg.IsReeferPowerRequired;dto.TargetTemperature=leg.TargetTemperature;
                dto.RequiredPowerKw=leg.RequiredPowerKw;dto.TemperatureUnit=leg.TemperatureUnit;dto.BookingCode=leg.BookingCode;}
        }
        dto.IsCombined=true;dto.BookingType=BookingType.Dropoff;
        dto.ContainerIds=dto.ContainerSelections.Select(selection=>selection.ContainerId).ToList();
        dto.DriverName=null;dto.VehiclePlate=null;dto.VehicleId=dto.TruckId;
        return total;
    }
}
