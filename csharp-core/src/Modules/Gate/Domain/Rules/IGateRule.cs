namespace NexusPort.Modules.Gate.Domain.Rules;

/// <summary>
/// Giao diện chuẩn cho mỗi quy tắc kiểm tra (Rule) tại cổng.
/// Hỗ trợ mở rộng không giới hạn các rule mới mà không cần sửa code cũ (Open/Closed Principle).
/// </summary>
public interface IGateRule
{
    /// <summary>
    /// Tên định danh của Rule (vd: BookingExistenceRule, VehicleMatchRule...)
    /// </summary>
    string RuleName { get; }

    /// <summary>
    /// Độ ưu tiên thực thi (số nhỏ hơn chạy trước).
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// Thực hiện đánh giá điều kiện dựa trên dữ liệu ngữ cảnh
    /// </summary>
    Task<GateRuleResult> EvaluateAsync(GateValidationContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Bộ điều phối thực thi các Rule kiểm tra Gate-In / Gate-Out
/// </summary>
public interface IGateRuleEngine
{
    /// <summary>
    /// Thực thi tất cả các rule theo thứ tự ưu tiên và trả về kết quả tổng hợp
    /// </summary>
    Task<GateRuleEvaluationResult> EvaluateAsync(GateValidationContext context, CancellationToken cancellationToken = default);
}
