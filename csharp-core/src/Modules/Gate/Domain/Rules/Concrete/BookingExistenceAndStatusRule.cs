namespace NexusPort.Modules.Gate.Domain.Rules.Concrete;

/// <summary>
/// Rule kiểm tra sự tồn tại và tính hợp lệ của trạng thái Booking
/// </summary>
public class BookingExistenceAndStatusRule : IGateRule
{
    public string RuleName => "BookingExistenceAndStatusRule";
    public int Priority => 10;

    public Task<GateRuleResult> EvaluateAsync(GateValidationContext context, CancellationToken cancellationToken = default)
    {
        if (context.Booking == null)
        {
            var msg = !string.IsNullOrWhiteSpace(context.BookingNumber)
                ? $"Không tìm thấy Booking với mã '{context.BookingNumber}' trong hệ thống."
                : $"Không tìm thấy Booking tương ứng với thông tin phương tiện '{context.VehiclePlate}'.";

            return Task.FromResult(GateRuleResult.Fail(RuleName, "BOOKING_NOT_FOUND", msg));
        }

        var status = context.Booking.Status;
        if (string.Equals(status, "Cancelled", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "Completed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "Expired", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(GateRuleResult.Fail(
                RuleName, 
                "BOOKING_INVALID_STATUS", 
                $"Booking '{context.Booking.BookingNumber}' không hợp lệ (Trạng thái hiện tại: {status})."));
        }

        return Task.FromResult(GateRuleResult.Pass(
            RuleName, 
            $"Booking '{context.Booking.BookingNumber}' hợp lệ (Trạng thái: {status})."));
    }
}
