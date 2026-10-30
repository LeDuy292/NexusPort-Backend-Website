using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NexusPort.Infrastructure.Authentication;
using NexusPort.Infrastructure.Database;
using NexusPort.Shared.Exceptions;

namespace NexusPort.Modules.Gate.Presentation.Controllers;

// Writes the canonical gate_transactions table. Its DB trigger closes exactly
// the linked Pickup stay and issues the immutable outgoing cargo snapshot.
[ApiController]
[Authorize(Roles = "Administrator,Admin,Gate Officer,Gate")]
[Route("api/v1/Gate/complete-out")]
public class YardGateOutController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly ICurrentUser _user;
    public YardGateOutController(AppDbContext context, ICurrentUser user) { _context = context; _user = user; }

    [HttpPost]
    public async Task<IActionResult> Complete([FromBody] CompleteYardGateOut request, CancellationToken cancellationToken)
    {
        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        var booking = await _context.Database.SqlQueryRaw<GateOutBooking>(
            "SELECT id AS \"Id\",status::text AS \"Status\",booking_type::text AS \"Type\" FROM bookings WHERE id={0} FOR UPDATE", request.BookingId).FirstOrDefaultAsync(cancellationToken);
        if (booking == null) throw new NotFoundException("Booking", request.BookingId);
        var bookingEntity = await _context.Set<Booking.Domain.Entities.Booking>()
            .Include(b => b.BookingOperations)
            .FirstOrDefaultAsync(b => b.Id == request.BookingId, cancellationToken);
        if (bookingEntity == null) throw new NotFoundException("Booking", request.BookingId);

        var previous = await _context.Database.SqlQueryRaw<Guid>(
            "SELECT id AS \"Value\" FROM gate_transactions WHERE booking_id={0} AND gate_type='gate_out' AND status='completed'", request.BookingId).ToListAsync(cancellationToken);
        if (previous.Count > 0) { await transaction.CommitAsync(cancellationToken); return Ok(new { id = previous[0] }); }

        // Find relevant GateOut operation
        var gateOutOp = bookingEntity.BookingOperations
            .FirstOrDefault(o => string.Equals(o.ExpectedGateDirection, "GateOut", StringComparison.OrdinalIgnoreCase));

        var isLegacyPickup = booking.Type == "pickup";
        if (gateOutOp == null && !isLegacyPickup)
        {
            throw new ValidationException("Booking", "Lịch hẹn không có tác nghiệp ra cổng (Gate Out) hợp lệ.");
        }

        var validGate = await _context.Database.SqlQueryRaw<Guid>("SELECT id AS \"Value\" FROM gates WHERE id={0} AND is_active", request.GateId).ToListAsync(cancellationToken);
        if (validGate.Count != 1) throw new ValidationException("Gate", "Cổng không hợp lệ hoặc đã ngừng hoạt động.");

        var id = Guid.NewGuid();
        var opId = gateOutOp?.Id;
        var contNo = gateOutOp?.ContainerNumber;

        await _context.Database.ExecuteSqlInterpolatedAsync($@"INSERT INTO gate_transactions(
            id, booking_id, gate_id, driver_id, truck_id, checked_by, gate_type, status, plate_number_detected, 
            checked_at, completed_at, booking_operation_id, container_number)
          SELECT {id}, id, {request.GateId}, driver_id, truck_id, {_user.UserId}, 'gate_out', 'completed', vehicle_plate, 
                 now(), now(), {opId}, {contNo} 
          FROM bookings WHERE id={request.BookingId}", cancellationToken);

        // Update booking operation and trip status
        if (gateOutOp != null)
        {
            gateOutOp.Complete($"EIR-OUT-{DateTime.UtcNow:yyyyMMdd}-{id.ToString().Substring(0, 8).ToUpper()}");
        }

        bookingEntity.UpdateDerivedStatus();
        bookingEntity.Status = Booking.Domain.Enums.BookingStatus.Completed;
        bookingEntity.TripStatus = "Completed";

        // Release yard slot if container was assigned in yard
        await _context.Database.ExecuteSqlInterpolatedAsync($@"WITH released AS (
          DELETE FROM container_positions p USING booking_containers bc
          WHERE p.container_id=bc.container_id AND bc.booking_id={request.BookingId} RETURNING p.slot_id)
          UPDATE yard_slots SET status='empty'::yard_slot_status WHERE id IN (SELECT slot_id FROM released)", cancellationToken);

        // Update container status to gate_out
        await _context.Database.ExecuteSqlInterpolatedAsync($@"UPDATE containers c
          SET status = 'gate_out'::container_status, left_at = now(), updated_at = now()
          FROM booking_containers bc
          WHERE c.id = bc.container_id AND bc.booking_id = {request.BookingId}", cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Ok(new { id, message = "Xác nhận Gate Out và hoàn tất chuyến xe thành công." });
    }
}
public class CompleteYardGateOut
{
    public Guid BookingId { get; set; }
    public Guid GateId { get; set; }
}
public class GateOutBooking
{
    public Guid Id { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
}
