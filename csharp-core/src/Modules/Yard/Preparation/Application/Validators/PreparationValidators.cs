using FluentValidation;
using NexusPort.Modules.Yard.Preparation.Application.DTOs;

namespace NexusPort.Modules.Yard.Preparation.Application.Validators;

public class CancelTaskRequestValidator : AbstractValidator<CancelTaskRequestDto>
{
    public CancelTaskRequestValidator()
    {
        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("Lý do hủy lệnh chuẩn bị không được để trống.")
            .MaximumLength(500).WithMessage("Lý do hủy không được vượt quá 500 ký tự.");
    }
}

public class ConfirmReadyRequestValidator : AbstractValidator<ConfirmReadyRequestDto>
{
    public ConfirmReadyRequestValidator()
    {
        RuleFor(x => x.ContainerNo)
            .NotEmpty().WithMessage("Số container thực tế không được để trống.")
            .Matches(@"^[A-Z]{4}\d{7}$").WithMessage("Số container không đúng định dạng tiêu chuẩn ISO 6346 (4 chữ cái + 7 chữ số).");

        RuleFor(x => x.SealNo)
            .NotEmpty().WithMessage("Số chì (seal) thực tế không được để trống.")
            .MaximumLength(50).WithMessage("Số chì không được vượt quá 50 ký tự.");
    }
}

public class AssignWorkOrderRequestValidator : AbstractValidator<AssignWorkOrderRequestDto>
{
    public AssignWorkOrderRequestValidator()
    {
        RuleFor(x => x)
            .Must(x => x.EquipmentId.HasValue || x.AssigneeId.HasValue)
            .WithMessage("Phải chọn ít nhất một thiết bị hoặc một nhân viên thực hiện lệnh.");
    }
}

public class CompleteWorkOrderRequestValidator : AbstractValidator<CompleteWorkOrderRequestDto>
{
    public CompleteWorkOrderRequestValidator()
    {
        RuleFor(x => x.ScannedContainerNo)
            .NotEmpty().WithMessage("Số container quét hoặc nhập không được để trống.")
            .Matches(@"^[A-Z]{4}\d{7}$").WithMessage("Số container không đúng định dạng tiêu chuẩn ISO 6346.");

        When(x => x.ActualToSlotId.HasValue && !string.IsNullOrWhiteSpace(x.ChangeSlotReason), () =>
        {
            RuleFor(x => x.ChangeSlotReason)
                .MaximumLength(500).WithMessage("Lý do đổi ô thực tế không được vượt quá 500 ký tự.");
        });
    }
}

public class FailWorkOrderRequestValidator : AbstractValidator<FailWorkOrderRequestDto>
{
    public FailWorkOrderRequestValidator()
    {
        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("Lý do báo lỗi lệnh làm hàng không được để trống.")
            .MaximumLength(500).WithMessage("Lý do báo lỗi không được vượt quá 500 ký tự.");
    }
}

public class PreparationConfigValidator : AbstractValidator<PreparationConfigDto>
{
    public PreparationConfigValidator()
    {
        RuleFor(x => x.LeadTimeMinutes)
            .GreaterThan(0).WithMessage("Thời gian chuẩn bị trước (LeadTimeMinutes) phải lớn hơn 0 phút.");

        RuleFor(x => x.MaxRehandlePerTask)
            .InclusiveBetween(1, 20).WithMessage("Số lần đảo chuyển tối đa mỗi lệnh phải từ 1 đến 20.");

        RuleFor(x => x.RescheduleGraceMinutes)
            .GreaterThanOrEqualTo(0).WithMessage("Thời gian gia hạn trễ (RescheduleGraceMinutes) không được âm.");

        RuleFor(x => x.DelayCheckIntervalSeconds)
            .InclusiveBetween(5, 3600).WithMessage("Khoảng thời gian kiểm tra trễ phải từ 5 đến 3600 giây.");

        RuleFor(x => x.EstimatedMinutesPerMove)
            .InclusiveBetween(1, 60).WithMessage("Thời gian ước tính mỗi lượt di chuyển phải từ 1 đến 60 phút.");
    }
}

public class CreatePreparationTaskRequestValidator : AbstractValidator<CreatePreparationTaskRequestDto>
{
    public CreatePreparationTaskRequestValidator()
    {
        RuleFor(x => x.SlotBookingId)
            .NotEmpty().WithMessage("Mã đặt chỗ (SlotBookingId) không được để trống.");

        RuleFor(x => x.ContainerNo)
            .NotEmpty().WithMessage("Số container không được để trống.")
            .Matches(@"^[A-Z]{4}\d{7}$").WithMessage("Số container không đúng định dạng tiêu chuẩn ISO 6346.");

        RuleFor(x => x.TargetReadyAt)
            .Must(t => t > DateTimeOffset.UtcNow)
            .WithMessage("Giờ hẹn lấy container phải lớn hơn thời gian hiện tại.");
    }
}
