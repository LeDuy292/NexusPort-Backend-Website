namespace NexusPort.Modules.Gate.Domain.Rules.Concrete;

/// <summary>
/// Rule kiểm tra tính phù hợp giữa tác nghiệp thực tế tại cổng và tác nghiệp đăng ký trong Booking (Check Operation)
/// </summary>
public class OperationMatchRule : IGateRule
{
    public string RuleName => "OperationMatchRule";
    public int Priority => 60;

    public Task<GateRuleResult> EvaluateAsync(GateValidationContext context, CancellationToken cancellationToken = default)
    {
        if (context.Booking == null)
        {
            return Task.FromResult(GateRuleResult.Pass(RuleName, "Bỏ qua kiểm tra Operation do không có Booking."));
        }

        var booking = context.Booking;
        var gateType = context.GateType; // "GateIn" hoặc "GateOut"
        var operation = context.OperationType ?? booking.GateType ?? "GateIn";

        // Kiểm tra chiều cổng GateIn / GateOut
        if (!string.IsNullOrWhiteSpace(booking.GateType))
        {
            if (string.Equals(gateType, "GateIn", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(booking.GateType, "GateOut", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(GateRuleResult.Fail(
                    RuleName,
                    "OPERATION_MISMATCH",
                    $"Booking '{booking.BookingNumber}' đăng ký cho luồng Gate-Out nhưng phương tiện đang thực hiện Gate-In."));
            }

            if (string.Equals(gateType, "GateOut", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(booking.GateType, "GateIn", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(GateRuleResult.Fail(
                    RuleName,
                    "OPERATION_MISMATCH",
                    $"Booking '{booking.BookingNumber}' đăng ký cho luồng Gate-In nhưng phương tiện đang thực hiện Gate-Out."));
            }
        }

        return Task.FromResult(GateRuleResult.Pass(
            RuleName,
            $"Tác nghiệp '{operation}' phù hợp với luồng {gateType} của Booking '{booking.BookingNumber}'."));
    }
}
