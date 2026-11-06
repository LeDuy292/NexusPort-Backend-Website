namespace NexusPort.Modules.Gate.Domain.Rules.Concrete;

/// <summary>
/// Rule kiểm tra xác nhận của tài xế đối với chuyến xe trước khi vào cảng (Check Driver Confirmation)
/// </summary>
public class DriverConfirmationRule : IGateRule
{
    public string RuleName => "DriverConfirmationRule";
    public int Priority => 70;

    public Task<GateRuleResult> EvaluateAsync(GateValidationContext context, CancellationToken cancellationToken = default)
    {
        // Nếu hệ thống hoặc request có truyền DriverConfirmed = false
        if (context.DriverConfirmed.HasValue && !context.DriverConfirmed.Value)
        {
            return Task.FromResult(GateRuleResult.Fail(
                RuleName,
                "DRIVER_NOT_CONFIRMED",
                "Tài xế chưa xác nhận thông tin chuyến đi trên ứng dụng Driver App."));
        }

        // Mặc định hợp lệ nếu tài xế đã xác nhận hoặc chế độ xác nhận tự động
        return Task.FromResult(GateRuleResult.Pass(
            RuleName,
            "Xác nhận chuyến đi từ tài xế hợp lệ."));
    }
}
