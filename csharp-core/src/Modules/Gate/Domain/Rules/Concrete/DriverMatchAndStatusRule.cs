namespace NexusPort.Modules.Gate.Domain.Rules.Concrete;

/// <summary>
/// Rule kiểm tra thông tin tài xế đối chiếu với Booking và trạng thái hoạt động của tài xế
/// </summary>
public class DriverMatchAndStatusRule : IGateRule
{
    public string RuleName => "DriverMatchAndStatusRule";
    public int Priority => 40;

    public Task<GateRuleResult> EvaluateAsync(GateValidationContext context, CancellationToken cancellationToken = default)
    {
        // 1. Đối chiếu họ tên tài xế nếu cả 2 bên đều có thông tin
        if (context.Booking != null && 
            !string.IsNullOrWhiteSpace(context.Booking.DriverName) && 
            !string.IsNullOrWhiteSpace(context.DriverName))
        {
            var expectedDriver = context.Booking.DriverName.Trim();
            var actualDriver = context.DriverName.Trim();

            if (!string.Equals(expectedDriver, actualDriver, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(GateRuleResult.Fail(
                    RuleName,
                    "DRIVER_MISMATCH",
                    $"Tài xế thực tế '{actualDriver}' không khớp với tài xế chỉ định trong Booking '{expectedDriver}'."));
            }
        }

        // 2. Kiểm tra trạng thái hoạt động của tài xế trong hệ thống nếu có
        if (context.Driver != null)
        {
            var status = context.Driver.Status;
            if (string.Equals(status, "Suspended", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "Blacklisted", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "Blocked", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(GateRuleResult.Fail(
                    RuleName,
                    "DRIVER_SUSPENDED",
                    $"Tài xế '{context.Driver.FullName}' đang bị tạm đình chỉ/khóa tài khoản (Trạng thái: {status})."));
            }

            if (string.Equals(status, "Inactive", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(GateRuleResult.Fail(
                    RuleName,
                    "DRIVER_INACTIVE",
                    $"Tài xế '{context.Driver.FullName}' không ở trạng thái hoạt động (Trạng thái: {status})."));
            }
        }

        var driverName = context.Driver?.FullName ?? context.DriverName ?? context.Booking?.DriverName ?? "Chưa chỉ định";
        return Task.FromResult(GateRuleResult.Pass(
            RuleName,
            $"Thông tin tài xế '{driverName}' hợp lệ."));
    }
}
