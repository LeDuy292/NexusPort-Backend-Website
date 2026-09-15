namespace NexusPort.Modules.Gate.Domain.Rules.Concrete;

/// <summary>
/// Rule kiểm tra tình trạng thanh toán cước nâng hạ / phí dịch vụ cổng (Check Billing)
/// </summary>
public class BillingAndPaymentStatusRule : IGateRule
{
    public string RuleName => "BillingAndPaymentStatusRule";
    public int Priority => 80;

    public Task<GateRuleResult> EvaluateAsync(GateValidationContext context, CancellationToken cancellationToken = default)
    {
        // 1. Kiểm tra cờ thanh toán
        if (context.BillingSettled.HasValue && !context.BillingSettled.Value)
        {
            return Task.FromResult(GateRuleResult.Fail(
                RuleName,
                "BILLING_UNPAID",
                "Chưa hoàn tất thanh toán phí nâng hạ / cước cổng cho chuyến xe này."));
        }

        // 2. Kiểm tra chuỗi trạng thái Billing
        if (!string.IsNullOrWhiteSpace(context.BillingStatus))
        {
            var status = context.BillingStatus.Trim().ToUpperInvariant();
            if (status == "UNPAID" || status == "OVERDUE" || status == "DEBT_EXCEEDED")
            {
                return Task.FromResult(GateRuleResult.Fail(
                    RuleName,
                    "BILLING_PAYMENT_REQUIRED",
                    $"Tình trạng cước phí không hợp lệ ({status}). Vui lòng thanh toán trước khi qua cổng."));
            }
        }

        return Task.FromResult(GateRuleResult.Pass(
            RuleName,
            "Tình trạng cước phí và thanh toán hợp lệ để qua cổng."));
    }
}
