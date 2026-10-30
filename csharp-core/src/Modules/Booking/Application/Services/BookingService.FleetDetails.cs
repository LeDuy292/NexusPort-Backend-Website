using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexusPort.Modules.Booking.Application.DTOs;
using NexusPort.Shared.Exceptions;

namespace NexusPort.Modules.Booking.Application.Services;

public partial class BookingService
{
    private static AvailableTruckDto VehicleDetails(Vehicle.Domain.Entities.Vehicle vehicle) => new()
    {
        Id=vehicle.Id, CarrierId=vehicle.CarrierId, DriverId=vehicle.DriverId,
        PlateNumber=vehicle.PlateNumber, VehicleType=vehicle.VehicleType, Status=vehicle.Status.ToString(),
        RfidTag=vehicle.RfidTag, Description=vehicle.Description, CurrentLocation=vehicle.CurrentLocation,
        PhotoUrl=FleetDocumentSource(vehicle.PhotoUrl), RegistrationImageUrl=FleetDocumentSource(vehicle.RegistrationImageUrl), CreatedAt=vehicle.CreatedAt
    };

    private static AvailableDriverDto DriverDetails(Driver.Domain.Entities.Driver driver) => new()
    {
        Id=driver.Id, CarrierId=driver.CarrierId, FullName=driver.FullName, Phone=driver.Phone,
        IdCardNumber=driver.IdCardNumber, LicenseNumber=driver.LicenseNumber, LicenseClass=driver.LicenseNumber,
        Status=driver.Status.ToString(), IdCardExpiryDate=driver.IdCardExpiryDate, LicenseExpiryDate=driver.LicenseExpiryDate,
        PhotoUrl=FleetDocumentSource(driver.PhotoUrl), IdCardFrontUrl=FleetDocumentSource(driver.IdCardFrontUrl), IdCardBackUrl=FleetDocumentSource(driver.IdCardBackUrl),
        LicenseImageUrl=FleetDocumentSource(driver.LicenseImageUrl), LicenseBackImageUrl=FleetDocumentSource(driver.LicenseBackImageUrl), CreatedAt=driver.CreatedAt
    };

    // Called inside the booking transaction after locking and validating the selected fleet.
    // IDs are authoritative: client names, plates and document URLs cannot overwrite the source profile.
    private async Task CaptureFleetDetailsAsync(Domain.Entities.Booking booking, CancellationToken token)
    {
        var previousVehicle=ReadFleetDetails<BookingVehicleDetailsDto>(booking.VehicleSnapshot);
        if (booking.TruckId.HasValue)
        {
            if (previousVehicle?.Id != booking.TruckId)
            {
                var truck=await _context.Set<Vehicle.Domain.Entities.Vehicle>().AsNoTracking()
                    .SingleOrDefaultAsync(t=>t.Id==booking.TruckId && t.CarrierId==booking.CarrierId,token)
                    ?? throw new ValidationException("TruckId","Xe không thuộc đội xe của công ty.");
                var details=VehicleDetails(truck); details.CapturedAt=DateTime.UtcNow;
                booking.VehicleSnapshot=JsonSerializer.Serialize<BookingVehicleDetailsDto>(details);
                previousVehicle=details;
            }
            booking.VehicleId=booking.TruckId; booking.VehiclePlate=previousVehicle!.PlateNumber;
        }
        else { booking.VehicleSnapshot=null; booking.VehicleId=null; booking.VehiclePlate=null; }

        var previousDriver=ReadFleetDetails<BookingDriverDetailsDto>(booking.DriverSnapshot);
        if (booking.DriverId.HasValue)
        {
            if (previousDriver?.Id != booking.DriverId)
            {
                var driver=await _context.Set<Driver.Domain.Entities.Driver>().AsNoTracking()
                    .SingleOrDefaultAsync(d=>d.Id==booking.DriverId && d.CarrierId==booking.CarrierId,token)
                    ?? throw new ValidationException("DriverId","Tài xế không thuộc công ty.");
                var details=DriverDetails(driver); details.CapturedAt=DateTime.UtcNow;
                booking.DriverSnapshot=JsonSerializer.Serialize<BookingDriverDetailsDto>(details);
                previousDriver=details;
            }
            booking.DriverName=previousDriver!.FullName;
        }
        else { booking.DriverSnapshot=null; booking.DriverName=null; }
    }

    private static T? ReadFleetDetails<T>(string? json) where T:class =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<T>(json);

    private void SignFleetDocuments(BookingVehicleDetailsDto? truck, BookingDriverDetailsDto? driver)
    {
        if (truck != null)
        {
            truck.PhotoUrl=FleetDocumentUrl(truck.PhotoUrl);
            truck.RegistrationImageUrl=FleetDocumentUrl(truck.RegistrationImageUrl);
        }
        if (driver != null)
        {
            driver.PhotoUrl=FleetDocumentUrl(driver.PhotoUrl);
            driver.IdCardFrontUrl=FleetDocumentUrl(driver.IdCardFrontUrl);
            driver.IdCardBackUrl=FleetDocumentUrl(driver.IdCardBackUrl);
            driver.LicenseImageUrl=FleetDocumentUrl(driver.LicenseImageUrl);
            driver.LicenseBackImageUrl=FleetDocumentUrl(driver.LicenseBackImageUrl);
        }
    }

    private string FleetDocumentUrl(string? source) => _fleetStorage.GetPresignedUrl(FleetDocumentSource(source));

    private static string? FleetDocumentSource(string? source)
    {
        // Legacy fleet profiles may contain an expired S3 signed URL rather than an object key.
        // Sign its original object URL again; shared storage otherwise returns an old signature unchanged.
        if (Uri.TryCreate(source,UriKind.Absolute,out var uri) &&
            (uri.Host.EndsWith(".amazonaws.com",StringComparison.OrdinalIgnoreCase) ||
             uri.Host.EndsWith(".amazonaws.com.cn",StringComparison.OrdinalIgnoreCase)) &&
            (uri.Host.Contains(".s3",StringComparison.OrdinalIgnoreCase) || uri.Host.StartsWith("s3.",StringComparison.OrdinalIgnoreCase)) &&
            !string.IsNullOrEmpty(uri.Query))
            source=uri.GetLeftPart(UriPartial.Path);
        return source;
    }
}
