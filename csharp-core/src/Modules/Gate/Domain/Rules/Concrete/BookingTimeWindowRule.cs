namespace NexusPort.Modules.Gate.Domain.Rules.Concrete;

/// <summary>
/// Rule kiểm tra thời gian hiệu lực của Booking (ValidFrom - ValidTo)
/// </summary>
public class BookingTimeWindowRule : IGateRule
{
    public string RuleName => "BookingTimeWindowRule";
    public int Priority => 20;

    public Task<GateRuleResult> EvaluateAsync(GateValidationContext context, CancellationToken cancellationToken = default)
    {
        if (context.Booking == null)
        {
            // Bỏ qua nếu không có booking (BookingExistenceRule đã bắt trước đó)
            return Task.FromResult(GateRuleResult.Pass(RuleName, "Bỏ qua kiểm tra thời gian do không có Booking."));
        }

        var booking = context.Booking;
        var checkTime = context.VerificationTime;

        if (booking.ValidTo.HasValue && checkTime > booking.ValidTo.Value)
        {
            return Task.FromResult(GateRuleResult.Fail(
                RuleName, 
                "BOOKING_EXPIRED", 
                $"Booking '{booking.BookingNumber}' đã hết hạn hiệu lực lúc {booking.ValidTo.Value:yyyy-MM-dd HH:mm:ss}."));
        }

        if (booking.ValidFrom.HasValue && checkTime < booking.ValidFrom.Value)
        {
            return Task.FromResult(GateRuleResult.Fail(
                RuleName, 
                "BOOKING_NOT_YET_VALID", 
                $"Booking '{booking.BookingNumber}' chưa đến thời gian hiệu lực (Bắt đầu từ {booking.ValidFrom.Value:yyyy-MM-dd HH:mm:ss})."));
        }

        return Task.FromResult(GateRuleResult.Pass(
            RuleName, 
            $"Thời gian xác thực ({checkTime:yyyy-MM-dd HH:mm:ss}) nằm trong khung giờ hiệu lực của Booking."));
    }
}
