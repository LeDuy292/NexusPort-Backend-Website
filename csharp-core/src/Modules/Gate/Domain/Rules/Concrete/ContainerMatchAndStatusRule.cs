using NexusPort.Modules.Gate.Domain.Validation;

namespace NexusPort.Modules.Gate.Domain.Rules.Concrete;

/// <summary>
/// Rule kiểm tra tính hợp lệ (ISO 6346), đối khớp Booking và trạng thái của Container trước khi Gate-In hoặc Gate-Out
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

        var containerNo = Iso6346Validator.Normalize(context.ContainerNumber);

        // 1. Kiểm tra định dạng & chữ số kiểm tra ISO 6346 khi quét từ Camera AI / kiểm duyệt container
        if (context.StrictContainerIsoCheck)
        {
            var isoCheck = Iso6346Validator.Validate(containerNo);
            if (!isoCheck.IsValid)
            {
                return Task.FromResult(GateRuleResult.Fail(
                    RuleName,
                    "CONTAINER_INVALID_ISO6346",
                    isoCheck.ErrorMessage ?? $"Mã container '{containerNo}' không hợp lệ theo chuẩn ISO 6346."));
            }
        }

        // 2. Đối chiếu số container với danh sách container đã đăng ký trong Booking
        if (context.ExpectedBookingContainerNumbers.Count > 0)
        {
            var expectedNormalized = context.ExpectedBookingContainerNumbers
                .Select(Iso6346Validator.Normalize)
                .Where(x => !string.IsNullOrEmpty(x))
                .ToList();

            if (expectedNormalized.Count > 0 && !expectedNormalized.Contains(containerNo, StringComparer.OrdinalIgnoreCase))
            {
                return Task.FromResult(GateRuleResult.Fail(
                    RuleName,
                    "CONTAINER_BOOKING_MISMATCH",
                    $"Số container '{containerNo}' không khớp với danh sách container đã đăng ký trong Booking ({string.Join(", ", expectedNormalized)})."));
            }
        }

        // 3. Đối chiếu loại/kích cỡ ISO Size/Type (nếu camera AI container.onnx đọc được vùng Type)
        if (!string.IsNullOrWhiteSpace(context.DetectedContainerType) && context.ExpectedBookingIsoSizes.Count > 0)
        {
            var compatible = context.ExpectedBookingIsoSizes.Any(expected =>
                Iso6346Validator.AreIsoTypesCompatible(context.DetectedContainerType, expected));

            if (!compatible)
            {
                return Task.FromResult(GateRuleResult.Fail(
                    RuleName,
                    "CONTAINER_TYPE_MISMATCH",
                    $"Loại container nhận diện '{context.DetectedContainerType}' không khớp loại container đăng ký ({string.Join(", ", context.ExpectedBookingIsoSizes)})."));
            }
        }

        // 4. Kiểm tra trạng thái Container trong hệ thống nếu có thông tin thực thể Container
        if (context.Container != null)
        {
            var status = context.Container.Status.Replace("_", "");

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

                if (string.Equals(status, "Damaged_Unsafe", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(status, "Damaged", StringComparison.OrdinalIgnoreCase))
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
