using NexusPort.Modules.Booking.Application.DTOs;

namespace NexusPort.Modules.Booking.Application.Services;

public class OperationCombinationValidationResult
{
    public bool IsValid { get; set; } = true;
    public string? ErrorMessage { get; set; }
    public string? CombinationType { get; set; }
    public string? SummaryVi { get; set; }
}

public interface IBookingCompatibilityPolicy
{
    OperationCombinationValidationResult ValidateCombination(IReadOnlyList<BookingOperationDto> operations);
    OperationCombinationValidationResult ValidateCombination(IReadOnlyList<CreateBookingOperationDto> operations);
}

public class BookingCompatibilityPolicy : IBookingCompatibilityPolicy
{
    public OperationCombinationValidationResult ValidateCombination(IReadOnlyList<BookingOperationDto> operations)
    {
        var mapped = operations.Select(o => new CreateBookingOperationDto
        {
            OperationType = o.OperationType,
            ExpectedGateDirection = o.ExpectedGateDirection,
            ContainerNumber = o.ContainerNumber,
            CargoStatus = o.CargoStatus,
            CargoType = o.CargoType,
            SequenceNumber = o.SequenceNumber
        }).ToList();

        return ValidateCombination(mapped);
    }

    public OperationCombinationValidationResult ValidateCombination(IReadOnlyList<CreateBookingOperationDto> operations)
    {
        if (operations == null || operations.Count == 0)
        {
            return new OperationCombinationValidationResult
            {
                IsValid = false,
                ErrorMessage = "Booking phải có ít nhất một tác nghiệp container (Xuất khẩu hoặc Nhập khẩu)."
            };
        }

        foreach (var op in operations)
        {
            if (IsType(op, "EmptyPickup") || IsType(op, "EmptyReturn") || string.Equals(op.CargoStatus, "Empty", StringComparison.OrdinalIgnoreCase))
            {
                return new OperationCombinationValidationResult
                {
                    IsValid = false,
                    ErrorMessage = "Hệ thống chỉ hỗ trợ 2 trường hợp xuất nhập khẩu (Xuất khẩu và Nhập khẩu hàng). Không hỗ trợ tác nghiệp container rỗng."
                };
            }

            if (!IsType(op, "Export") && !IsType(op, "Import"))
            {
                return new OperationCombinationValidationResult
                {
                    IsValid = false,
                    ErrorMessage = $"Tác nghiệp '{op.OperationType}' không hợp lệ. Hệ thống chỉ hỗ trợ 2 trường hợp: Xuất khẩu (Export) và Nhập khẩu (Import)."
                };
            }
        }

        if (operations.Count == 1)
        {
            var op = operations[0];
            return new OperationCombinationValidationResult
            {
                IsValid = true,
                CombinationType = $"Single_{op.OperationType}",
                SummaryVi = $"Tác nghiệp đơn: {GetOperationNameVi(op.OperationType)} (Container: {op.ContainerNumber})"
            };
        }

        if (operations.Count > 2)
        {
            return new OperationCombinationValidationResult
            {
                IsValid = false,
                ErrorMessage = "Mỗi chuyến xe (Port Visit) tối đa hỗ trợ kết hợp 2 tác nghiệp (1 chặng vào Gate In và 1 chặng ra Gate Out)."
            };
        }

        var op1 = operations.OrderBy(o => o.SequenceNumber).First();
        var op2 = operations.OrderBy(o => o.SequenceNumber).Last();

        var hasGateIn = string.Equals(op1.ExpectedGateDirection, "GateIn", StringComparison.OrdinalIgnoreCase);
        var hasGateOut = string.Equals(op2.ExpectedGateDirection, "GateOut", StringComparison.OrdinalIgnoreCase);

        if (!hasGateIn || !hasGateOut)
        {
            return new OperationCombinationValidationResult
            {
                IsValid = false,
                ErrorMessage = "Chuyến kết hợp hợp lệ phải bắt đầu bằng chặng vào cảng (Gate In) và kết thúc bằng chặng ra cảng (Gate Out)."
            };
        }

        if (!string.IsNullOrWhiteSpace(op1.ContainerNumber) &&
            !string.IsNullOrWhiteSpace(op2.ContainerNumber) &&
            string.Equals(op1.ContainerNumber.Trim(), op2.ContainerNumber.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return new OperationCombinationValidationResult
            {
                IsValid = false,
                ErrorMessage = $"Container '{op1.ContainerNumber}' không thể vừa giao vào và vừa bốc ra trong cùng một chuyến xe kết hợp."
            };
        }

        if (IsType(op1, "Export") && IsType(op2, "Import"))
        {
            return new OperationCombinationValidationResult
            {
                IsValid = true,
                CombinationType = "Combined_Export_Import",
                SummaryVi = $"Chuyến kết hợp: Hạ cont xuất khẩu ({op1.ContainerNumber}) & Bốc cont hàng nhập ({op2.ContainerNumber})"
            };
        }

        return new OperationCombinationValidationResult
        {
            IsValid = false,
            ErrorMessage = $"Tổ hợp tác nghiệp '{op1.OperationType}' (Vào) và '{op2.OperationType}' (Ra) không phù hợp. Chuyến kết hợp hai chiều chỉ hỗ trợ: Xuất khẩu (Gate In) + Nhập khẩu (Gate Out)."
        };
    }

    private static bool IsType(CreateBookingOperationDto op, string expectedType)
    {
        return string.Equals(op.OperationType, expectedType, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetOperationNameVi(string opType) => opType?.ToLowerInvariant() switch
    {
        "export" => "Xuất khẩu (Hạ cont hàng)",
        "import" => "Nhập khẩu (Bốc cont hàng)",
        _ => opType ?? "Không xác định"
    };
}
