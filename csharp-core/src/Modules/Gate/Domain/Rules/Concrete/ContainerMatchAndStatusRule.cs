using System.Text.RegularExpressions;

namespace NexusPort.Modules.Gate.Domain.Rules.Concrete;

/// <summary>
/// Rule kiểm tra tính hợp lệ và trạng thái của Container trước khi cho phép Gate-In hoặc Gate-Out
/// </summary>
public class ContainerMatchAndStatusRule : IGateRule
{
    public string RuleName => "ContainerMatchAndStatusRule";
    public int Priority => 50;

    public Task<GateRuleResult> EvaluateAsync(GateValidationContext context, CancellationToken cancellationToken = default)
    {
        // Nếu chuyến xe không chở container (xe rỗng, bobtail vào bốc hàng)
        if (string.IsNullOrWhiteSpace(context.ContainerNumber))
        {
            return Task.FromResult(GateRuleResult.Pass(
                RuleName, 
                "Phương tiện không chở container (xe rỗng / vào nhận hàng) - Hợp lệ."));
        }

        var containerNo = context.ContainerNumber.Trim().ToUpperInvariant();

        // 1. Kiểm tra trạng thái Container trong hệ thống nếu có thông tin thực thể Container
        if (context.Container != null)
        {
            var status = context.Container.Status;

            // Nghiệp vụ Gate-In: Container vào cảng
            if (string.Equals(context.GateType, "GateIn", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(status, "InYard", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(status, "Stacked", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(GateRuleResult.Fail(
                        RuleName,
                        "CONTAINER_ALREADY_IN_YARD",
                        $"Container '{containerNo}' đã được ghi nhận đang lưu tại bãi cảng, không thể thực hiện Gate-In."));
                }
            }
            // Nghiệp vụ Gate-Out: Container xuất bãi ra ngoài
            else if (string.Equals(context.GateType, "GateOut", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(status, "Hold", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(status, "CustomsHold", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(GateRuleResult.Fail(
                        RuleName,
                        "CONTAINER_ON_HOLD",
                        $"Container '{containerNo}' đang bị giữ bởi Hải quan/Cảng vụ (Trạng thái: {status}), chưa đủ điều kiện Gate-Out."));
                }

                if (string.Equals(status, "Damaged_Unsafe", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(GateRuleResult.Fail(
                        RuleName,
                        "CONTAINER_DAMAGED",
                        $"Container '{containerNo}' đang ở trạng thái hư hỏng không an toàn vận chuyển (Trạng thái: {status})."));
                }
            }

            if (string.Equals(status, "Inactive", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "Blocked", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(GateRuleResult.Fail(
                    RuleName,
                    "CONTAINER_INVALID_STATUS",
                    $"Container '{containerNo}' đang ở trạng thái bị khóa hoặc không hoạt động (Trạng thái: {status})."));
            }
        }

        return Task.FromResult(GateRuleResult.Pass(
            RuleName,
            $"Container '{containerNo}' hợp lệ cho luồng {context.GateType}."));
    }
}
