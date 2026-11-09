namespace NexusPort.Modules.Gate.Domain.Rules;

/// <summary>
/// Kết quả đánh giá của một Rule đơn lẻ
/// </summary>
public class GateRuleResult
{
    public bool IsSuccess { get; set; }
    public string RuleName { get; set; } = string.Empty;
    public string? FailureReason { get; set; }
    public string Message { get; set; } = string.Empty;

    public static GateRuleResult Pass(string ruleName, string message = "Đạt điều kiện kiểm tra")
    {
        return new GateRuleResult
        {
            IsSuccess = true,
            RuleName = ruleName,
            FailureReason = null,
            Message = message
        };
    }

    public static GateRuleResult Fail(string ruleName, string failureReason, string message)
    {
        return new GateRuleResult
        {
            IsSuccess = false,
            RuleName = ruleName,
            FailureReason = failureReason,
            Message = message
        };
    }
}

/// <summary>
/// Kết quả tổng hợp sau khi chạy toàn bộ pipeline của Rule Engine
/// </summary>
public class GateRuleEvaluationResult
{
    public bool IsSuccess { get; set; }
    public string Status => IsSuccess ? "PASS" : "FAIL";
    public string? FailedRuleName { get; set; }
    public string? FailureReason { get; set; }
    public string Message { get; set; } = string.Empty;
    public IReadOnlyList<GateRuleResult> RuleResults { get; set; } = Array.Empty<GateRuleResult>();
}
