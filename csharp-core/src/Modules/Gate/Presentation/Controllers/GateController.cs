using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NexusPort.Modules.Gate.Application.DTOs;
using NexusPort.Modules.Gate.Application.Interfaces;

namespace NexusPort.Modules.Gate.Presentation.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class GateController : ControllerBase
{
    private readonly IGateService _service;
    private readonly IGateVerificationService _verificationService;
    private readonly IGateContainerInspectionService _containerInspectionService;

    public GateController(
        IGateService service,
        IGateVerificationService verificationService,
        IGateContainerInspectionService containerInspectionService)
    {
        _service = service;
        _verificationService = verificationService;
        _containerInspectionService = containerInspectionService;
    }

    /// <summary>
    /// API tiếp nhận sự kiện nhận diện (Gate Recognition Event) từ AI Camera / YOLO / Cảm biến RFID và đối chiếu điều kiện qua Rule Engine
    /// </summary>
    [HttpPost("verify")]
    [Authorize(Roles = "Administrator,Admin,Gate Officer,Gate")]
    [HttpPost("recognition-event")]
    [ProducesResponseType(typeof(GateVerificationResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<GateVerificationResultDto>> ProcessRecognitionEvent(
        [FromBody] GateRecognitionEventDto request, 
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var result = await _verificationService.VerifyGateScanAsync(request, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// API tiếp nhận sự kiện nhận diện từ thiết bị IoT / Camera tại cổng (Hỗ trợ gọi trực tiếp từ vi điều khiển / ESP32 / Raspberry Pi)
    /// </summary>
    [HttpPost("iot/recognition-event")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(GateVerificationResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<GateVerificationResultDto>> ProcessIotRecognitionEvent(
        [FromBody] GateRecognitionEventDto request, 
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var result = await _verificationService.VerifyGateScanAsync(request, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Kiểm tra trước (Pre-check) toàn diện các điều kiện phương tiện, tài xế, container và booking qua Rule Engine
    /// </summary>
    [HttpPost("rules/evaluate")]
    [ProducesResponseType(typeof(Domain.Rules.GateRuleEvaluationResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<Domain.Rules.GateRuleEvaluationResult>> EvaluateGateRules(
        [FromBody] GateRulePreCheckRequestDto request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var result = await _verificationService.EvaluateRulesAsync(request, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Tra cứu danh sách lịch sử xác thực tại cổng với các bộ lọc
    /// </summary>
    [HttpGet("verifications")]
    [ProducesResponseType(typeof(IReadOnlyList<GateVerificationRecordDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<GateVerificationRecordDto>>> GetVerificationHistory(
        [FromQuery] GateVerificationFilterDto filter, 
        CancellationToken cancellationToken)
    {
        var records = await _verificationService.GetListAsync(filter, cancellationToken);
        return Ok(records);
    }

    /// <summary>
    /// Lấy chi tiết bản ghi xác thực cổng theo ID
    /// </summary>
    [HttpGet("verifications/{id:guid}")]
    [ProducesResponseType(typeof(GateVerificationRecordDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<GateVerificationRecordDto>> GetVerificationById(
        Guid id, 
        CancellationToken cancellationToken)
    {
        var record = await _verificationService.GetByIdAsync(id, cancellationToken);
        if (record == null) return NotFound(new { message = $"Không tìm thấy bản ghi xác thực với ID '{id}'" });
        return Ok(record);
    }

    /// <summary>
    /// Ghi đè trạng thái xác thực thủ công bởi nhân viên kiểm soát cổng (Gate Officer)
    /// </summary>
    [HttpPost("verifications/{id:guid}/manual-override")]
    [Authorize(Roles = "Administrator,Admin,Gate Officer,Gate")]
    [ProducesResponseType(typeof(GateVerificationRecordDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<GateVerificationRecordDto>> ManualOverride(
        Guid id, 
        [FromBody] ManualOverrideDto dto, 
        CancellationToken cancellationToken)
    {
        var updated = await _verificationService.ManualOverrideAsync(id, dto, cancellationToken);
        if (updated == null) return NotFound(new { message = $"Không tìm thấy bản ghi xác thực với ID '{id}'" });
        return Ok(updated);
    }

    /// <summary>
    /// Phê duyệt Gate-In cho phương tiện vào cảng (Role: Gate Officer + Backend)
    /// Tự động cập nhật Booking=CheckedIn, Container=InYard, Vehicle=Inside, Lưu Timestamp và Gửi Notification
    /// </summary>
    [Authorize(Roles = "Administrator,Admin,Gate Officer,Gate")]
    [HttpPost("approve-in")]
    [HttpPost("passage-in")]
    [ProducesResponseType(typeof(GateInApprovalResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<GateInApprovalResultDto>> ApproveGateIn(
        [FromBody] GateInApprovalRequestDto request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var result = await _verificationService.ApproveGateInAsync(request, cancellationToken);
        return Ok(result);
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<GateTransactionDto>>> GetAll(CancellationToken cancellationToken)
    {
        var items = await _service.GetAllAsync(cancellationToken);
        return Ok(items);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<GateTransactionDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var item = await _service.GetByIdAsync(id, cancellationToken);
        if (item == null) return NotFound();
        return Ok(item);
    }

    [HttpPost]
    public async Task<ActionResult<GateTransactionDto>> Create([FromBody] CreateGateTransactionDto dto, CancellationToken cancellationToken)
    {
        var item = await _service.CreateAsync(dto, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = item.Id }, item);
    }

    /// <summary>
    /// Kiểm tra trạng thái mô hình AI ONNX nhận diện container (container.onnx) và cấu hình OCR.
    /// </summary>
    [HttpGet("containers/model-status")]
    [AllowAnonymous]
    public ActionResult<ContainerModelStatusDto> GetContainerModelStatus()
    {
        return Ok(_containerInspectionService.GetModelStatus());
    }

    /// <summary>
    /// Nhận diện và kiểm duyệt container từ ảnh chụp cổng Gate bằng model container.onnx + OCR + ISO 6346 + đối chiếu Booking.
    /// </summary>
    [HttpPost("containers/inspect")]
    [HttpPost("inspect-container")]
    [Authorize(Roles = "Administrator,Admin,Gate Officer,Gate")]
    [Consumes("multipart/form-data")]
    public async Task<ActionResult<ContainerInspectionResultDto>> InspectContainerImage(
        IFormFile file,
        [FromForm] string? bookingNumber = null,
        [FromForm] string? vehiclePlate = null,
        [FromForm] string? gateCode = "GATE-01",
        [FromForm] string? laneCode = "LANE_01",
        [FromForm] string? gateType = "GateIn",
        [FromForm] bool autoVerify = false,
        [FromForm] string? overviewImageUrl = null,
        CancellationToken cancellationToken = default)
    {
        if (file == null || file.Length == 0)
        {
            throw new NexusPort.Shared.Exceptions.ValidationException("File", "Vui lòng tải lên tệp ảnh container hợp lệ.");
        }

        if (file.Length > 20 * 1024 * 1024)
        {
            throw new NexusPort.Shared.Exceptions.ValidationException("File", "Kích thước ảnh container vượt quá giới hạn 20MB.");
        }

        await using var ms = new MemoryStream();
        await file.CopyToAsync(ms, cancellationToken);

        var query = new ContainerInspectionQuery
        {
            BookingNumber = bookingNumber,
            VehiclePlate = vehiclePlate,
            GateCode = gateCode,
            LaneCode = laneCode,
            GateType = gateType,
            AutoVerify = autoVerify,
            OverviewImageUrl = overviewImageUrl
        };

        var result = await _containerInspectionService.InspectImageAsync(ms.ToArray(), query, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Nhận diện và kiểm duyệt container từ chuỗi ảnh Base64 bằng model container.onnx + ISO 6346.
    /// </summary>
    [HttpPost("containers/inspect-base64")]
    [Authorize(Roles = "Administrator,Admin,Gate Officer,Gate")]
    public async Task<ActionResult<ContainerInspectionResultDto>> InspectContainerBase64(
        [FromBody] ContainerInspectImageBase64RequestDto request,
        CancellationToken cancellationToken = default)
    {
        var imageBytes = DecodeBase64Image(request.ImageBase64);
        var query = new ContainerInspectionQuery
        {
            BookingNumber = request.BookingNumber,
            VehiclePlate = request.VehiclePlate,
            GateCode = request.GateCode,
            LaneCode = request.LaneCode,
            GateType = request.GateType,
            AutoVerify = request.AutoVerify
        };

        var result = await _containerInspectionService.InspectImageAsync(imageBytes, query, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Gate Officer xác nhận Đạt hoặc Từ chối kiểm duyệt container, đồng thời ghi log GateVerificationRecord & BookingOperation.
    /// </summary>
    [HttpPost("containers/verify-decision")]
    [Authorize(Roles = "Administrator,Admin,Gate Officer,Gate")]
    public async Task<ActionResult<ContainerVerifyDecisionResultDto>> RecordContainerDecision(
        [FromBody] ContainerVerifyDecisionRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var result = await _containerInspectionService.RecordContainerDecisionAsync(request, cancellationToken);
        return Ok(result);
    }

    private static byte[] DecodeBase64Image(string? rawBase64)
    {
        if (string.IsNullOrWhiteSpace(rawBase64))
        {
            throw new NexusPort.Shared.Exceptions.ValidationException("ImageBase64", "Chuỗi ảnh imageBase64 không được để trống.");
        }

        var commaIndex = rawBase64.IndexOf(',');
        var payload = commaIndex >= 0 ? rawBase64[(commaIndex + 1)..] : rawBase64;
        try
        {
            var bytes = Convert.FromBase64String(payload.Trim());
            if (bytes.Length == 0)
            {
                throw new NexusPort.Shared.Exceptions.ValidationException("ImageBase64", "Dữ liệu ảnh Base64 rỗng.");
            }
            return bytes;
        }
        catch (FormatException)
        {
            throw new NexusPort.Shared.Exceptions.ValidationException("ImageBase64", "Định dạng chuỗi Base64 của ảnh container không hợp lệ.");
        }
    }
}
