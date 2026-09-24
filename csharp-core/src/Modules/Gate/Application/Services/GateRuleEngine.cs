using NexusPort.Modules.Gate.Domain.Rules;

namespace NexusPort.Modules.Gate.Application.Services;

/// <summary>
/// Bộ điều phối thực thi chuỗi quy tắc kiểm tra (Rule Pipeline) cho cổng cảng
/// </summary>
public class GateRuleEngine : IGateRuleEngine
{
    private readonly IEnumerable<IGateRule> _rules;

    public GateRuleEngine(IEnumerable<IGateRule> rules)
    {
        _rules = rules;
    }

    public async Task<GateRuleEvaluationResult> EvaluateAsync(GateValidationContext context, CancellationToken cancellationToken = default)
    {
        var orderedRules = _rules.OrderBy(r => r.Priority).ToList();
        var ruleResults = new List<GateRuleResult>();

        foreach (var rule in orderedRules)
        {
            var result = await rule.EvaluateAsync(context, cancellationToken);
            ruleResults.Add(result);

            if (!result.IsSuccess)
            {
                // Ngắt luồng ngay khi gặp rule không đạt (Fail-Fast)
                return new GateRuleEvaluationResult
                {
                    IsSuccess = false,
                    FailedRuleName = rule.RuleName,
                    FailureReason = result.FailureReason,
                    Message = result.Message,
                    RuleResults = ruleResults
                };
            }
        }

        // Tất cả các rule đều vượt qua thành công
        return new GateRuleEvaluationResult
        {
            IsSuccess = true,
            FailedRuleName = null,
            FailureReason = null,
            Message = "Tất cả các điều kiện phương tiện, tài xế, container và booking đều hợp lệ để qua cổng.",
            RuleResults = ruleResults
        };
    }
}
