using System.Text.RegularExpressions;

namespace NexusPort.Modules.Gate.Domain.Rules.Concrete;

/// <summary>
/// Rule kiểm tra tính chính xác của Biển số xe so với Booking và trạng thái hoạt động của phương tiện
/// </summary>
public class VehicleMatchAndStatusRule : IGateRule
{
    public string RuleName => "VehicleMatchAndStatusRule";
    public int Priority => 30;

    public Task<GateRuleResult> EvaluateAsync(GateValidationContext context, CancellationToken cancellationToken = default)
    {
        // 0. Kiểm tra cảm biến hiện diện phương tiện
        if (!context.VehicleDetected)
        {
            return Task.FromResult(GateRuleResult.Fail(
                RuleName,
                "NO_VEHICLE_DETECTED",
                "Cảm biến cổng không phát hiện phương tiện hiện diện tại làn kiểm soát."));
        }

        var normalizedDetected = Normalize(context.VehiclePlate);

        // 1. Đối chiếu với Booking (nếu Booking có quy định biển số)
        if (context.Booking != null && !string.IsNullOrWhiteSpace(context.Booking.VehiclePlate))
        {
            var normalizedExpected = Normalize(context.Booking.VehiclePlate);
            if (normalizedDetected != normalizedExpected)
            {
                return Task.FromResult(GateRuleResult.Fail(
                    RuleName,
                    "VEHICLE_MISMATCH",
                    $"Biển số xe '{context.VehiclePlate}' không khớp với Booking '{context.Booking.VehiclePlate}'."));
            }
        }

        // 2. Đối chiếu thẻ RFID (nếu xe trong hệ thống có đăng ký thẻ RFID)
        if (!string.IsNullOrWhiteSpace(context.RfidTag) && 
            context.Vehicle != null && 
            !string.IsNullOrWhiteSpace(context.Vehicle.RfidTag))
        {
            if (!string.Equals(context.RfidTag.Trim(), context.Vehicle.RfidTag.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(GateRuleResult.Fail(
                    RuleName,
                    "RFID_MISMATCH",
                    $"Mã thẻ RFID '{context.RfidTag}' không khớp với thẻ đăng ký của xe '{context.VehiclePlate}' (Đăng ký: '{context.Vehicle.RfidTag}')."));
            }
        }

        // 2. Kiểm tra trạng thái xe trong hệ thống nếu có thông tin Vehicle
        if (context.Vehicle != null)
        {
            var status = context.Vehicle.Status;
            if (string.Equals(status, "Blacklisted", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "Suspended", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "Blocked", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(GateRuleResult.Fail(
                    RuleName,
                    "VEHICLE_BLACKLISTED",
                    $"Phương tiện '{context.VehiclePlate}' đang bị tạm khóa/cấm vào cảng (Trạng thái: {status})."));
            }

            if (string.Equals(status, "Maintenance", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "Inactive", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(GateRuleResult.Fail(
                    RuleName,
                    "VEHICLE_INACTIVE",
                    $"Phương tiện '{context.VehiclePlate}' không ở trạng thái sẵn sàng hoạt động (Trạng thái: {status})."));
            }
        }

        return Task.FromResult(GateRuleResult.Pass(
            RuleName,
            $"Phương tiện '{context.VehiclePlate}' hợp lệ và ở trạng thái hoạt động bình thường."));
    }

    private static string Normalize(string? plate)
    {
        if (string.IsNullOrWhiteSpace(plate)) return string.Empty;
        return Regex.Replace(plate, @"[^a-zA-Z0-9]", string.Empty).ToUpperInvariant();
    }
}
