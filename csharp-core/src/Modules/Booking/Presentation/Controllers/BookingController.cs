using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using NexusPort.Infrastructure.Database;
using NexusPort.Shared.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Hosting;
using System.IO;
using NexusPort.Infrastructure.Authentication;
using NexusPort.Infrastructure.ExternalServices;
using NexusPort.Modules.Booking.Application.DTOs;
using NexusPort.Modules.Booking.Application.Interfaces;
using NexusPort.Shared.Results;

namespace NexusPort.Modules.Booking.Presentation.Controllers;

[Authorize(Roles = "Administrator,Admin,Dispatcher,Carrier,Transport Company,Carrier Staff,Driver,Gate Officer,Gate")]
[ApiController]
[Route("api/v1/[controller]")]
[Produces("application/json")]
public class BookingController : ControllerBase
{
    private readonly IBookingService _service;
    private readonly ICurrentUser _currentUser;
    private readonly AppDbContext _context;
    private readonly IS3StorageService _s3StorageService;
    private readonly IWebHostEnvironment _env;

    public BookingController(
        IBookingService service, 
        ICurrentUser currentUser, 
        AppDbContext context,
        IS3StorageService s3StorageService,
        IWebHostEnvironment env)
    {
        _service = service;
        _currentUser = currentUser;
        _context = context;
        _s3StorageService = s3StorageService;
        _env = env;
    }

    /// <summary>
    /// Lấy danh sách Booking hỗ trợ Tìm kiếm, Lọc và Phân trang (Search, Filter, Pagination)
    /// </summary>
    [Authorize(Roles = "Administrator,Admin,Dispatcher,Carrier,Transport Company,Carrier Staff,Gate Officer,Gate")]
    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<BookingDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<BookingDto>>> GetPaged(
        [FromQuery] BookingFilterParams filter,
        CancellationToken cancellationToken)
    {
        // Enforce Carrier tenant isolation if logged in as Carrier/TransportCompany
        if (IsCarrierRole())
        {
            filter.CarrierId = GetUserCarrierId();
        }

        var result = await _service.GetPagedAsync(filter, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Xem chi tiết thông tin Booking theo ID
    /// </summary>
    [Authorize(Roles = "Administrator,Admin,Dispatcher,Carrier,Transport Company,Carrier Staff,Gate Officer,Gate")]
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(BookingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<BookingDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        Guid? userCarrierId = IsCarrierRole() ? GetUserCarrierId() : null;

        var item = await _service.GetByIdAsync(id, userCarrierId, cancellationToken);
        if (item == null)
        {
            return NotFound(new { message = $"Booking with ID '{id}' was not found." });
        }

        return Ok(item);
    }

    /// <summary>
    /// Tạo Booking đặt lịch mới cho Transport Company
    /// </summary>
    [Authorize(Roles = "Administrator,Admin,Dispatcher,Carrier,Transport Company,Carrier Staff")]
    [HttpPost]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("booking-create")]
    [ProducesResponseType(typeof(BookingDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<BookingDto>> Create([FromBody] CreateBookingDto dto, CancellationToken cancellationToken)
    {
        // Auto-assign CarrierId if logged in as Carrier and not explicitly passed
        if (IsCarrierRole())
        {
            var userCarrierId = GetUserCarrierId();
            if (userCarrierId.HasValue)
            {
                dto.CarrierId = userCarrierId.Value;
            }
        }

        var item = await _service.CreateAsync(dto, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = item.Id }, item);
    }

    /// <summary>
    /// Cập nhật thông tin Booking (khi ở trạng thái Pending)
    /// </summary>
    [Authorize(Roles = "Administrator,Admin,Dispatcher,Carrier,Transport Company,Carrier Staff")]
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(BookingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<BookingDto>> Update(
        Guid id,
        [FromBody] UpdateBookingDto dto,
        CancellationToken cancellationToken)
    {
        Guid? userCarrierId = IsCarrierRole() ? GetUserCarrierId() : null;

        var item = await _service.UpdateAsync(id, dto, userCarrierId, cancellationToken);
        return Ok(item);
    }

    /// <summary>
    /// Hủy Booking theo Business Rules
    /// </summary>
    [Authorize(Roles = "Administrator,Admin,Dispatcher,Carrier,Transport Company,Carrier Staff")]
    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType(typeof(BookingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BookingDto>> Cancel(
        Guid id,
        [FromBody] CancelBookingDto? dto,
        CancellationToken cancellationToken)
    {
        Guid? userCarrierId = IsCarrierRole() ? GetUserCarrierId() : null;

        var item = await _service.CancelAsync(id, dto ?? new CancelBookingDto(), userCarrierId, cancellationToken);
        return Ok(item);
    }

    [HttpGet("driver/operations")]
    [ProducesResponseType(typeof(IReadOnlyList<DriverContainerOperationDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<DriverContainerOperationDto>>> GetDriverOperations(CancellationToken cancellationToken)
    {
        if (!_currentUser.UserId.HasValue)
            return Unauthorized(new { message = "Driver authentication is required." });

        return Ok(await _service.GetDriverOperationsAsync(_currentUser.UserId.Value, cancellationToken));
    }

    [HttpPost("driver/container-confirmation")]
    [ProducesResponseType(typeof(ContainerConfirmationResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ContainerConfirmationResultDto>> ConfirmContainer(
        [FromBody] ContainerConfirmationDto dto,
        CancellationToken cancellationToken)
    {
        if (!_currentUser.UserId.HasValue)
            return Unauthorized(new { message = "Driver authentication is required." });

        var result = await _service.ConfirmContainerAsync(dto, _currentUser.UserId.Value, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// NXP-048: Lấy danh sách tài nguyên sẵn sàng (Containers trong bãi, Xe đầu kéo kèm tải trọng, Tài xế active) phục vụ AI Auto-Match
    /// </summary>
    [Authorize(Roles = "Administrator,Admin,Dispatcher,Carrier,Transport Company,Carrier Staff")]
    [HttpGet("available-resources")]
    [ProducesResponseType(typeof(AvailableFleetResourcesDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<AvailableFleetResourcesDto>> GetAvailableResources(CancellationToken cancellationToken)
    {
        Guid? userCarrierId = IsCarrierRole() ? GetUserCarrierId() : null;
        var result = await _service.GetAvailableResourcesAsync(userCarrierId, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// NXP-048: AI Gợi ý tối ưu xe đầu kéo, tài xế và khung giờ hẹn dựa trên container (xử lý hoàn toàn tại Backend và CSDL)
    /// </summary>
    [Authorize(Roles = "Administrator,Admin,Dispatcher,Carrier,Transport Company,Carrier Staff")]
    [HttpPost("recommend-selection")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("booking-create")]
    public async Task<ActionResult<FleetRecommendationDto>> RecommendSelection([FromBody] BookingSelectionRecommendationDto selection, CancellationToken cancellationToken)
    {
        if (!IsCarrierRole()) throw new UnauthorizedException("Chỉ công ty vận tải phân tích cho lần giao nhận của mình.");
        var carrierId = GetUserCarrierId();
        if (selection.BookingId.HasValue)
        {
            var booking = await _service.GetByIdAsync(selection.BookingId.Value, carrierId, cancellationToken);
            var link = booking?.ContainerSelections.SingleOrDefault(x => x.ContainerId == selection.ContainerId);
            if (link == null) throw new ValidationException("Container", "Container không thuộc booking đang điều phối.");
            // The booked private declaration is already scoped to this company.
            return Ok(await _service.RecommendFleetAsync(selection.ContainerId, "Dropoff", carrierId, cancellationToken, link.DeclarationId));
        }
        return Ok(await _service.RecommendFleetAsync(selection.ContainerId, selection.BookingType, carrierId, cancellationToken, selection.DeclarationId, selection.YardCode));
    }

    [Authorize(Roles = "Administrator,Admin,Dispatcher,Carrier,Transport Company,Carrier Staff")]
    [HttpGet("recommend-fleet")]
    [ProducesResponseType(typeof(FleetRecommendationDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<FleetRecommendationDto>> RecommendFleet(
        [FromQuery] Guid? containerId,
        [FromQuery] string? bookingType,
        CancellationToken cancellationToken)
    {
        Guid? userCarrierId = IsCarrierRole() ? GetUserCarrierId() : null;
        var result = await _service.RecommendFleetAsync(containerId, bookingType, userCarrierId, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// NXP-048: Đánh giá tỷ lệ tải trọng và cảnh báo an toàn từ Backend trực tiếp từ CSDL
    /// </summary>
    [Authorize(Roles = "Administrator,Admin,Dispatcher,Carrier,Transport Company,Carrier Staff")]
    [HttpGet("evaluate-payload")]
    [ProducesResponseType(typeof(PayloadEvaluationDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<PayloadEvaluationDto>> EvaluatePayload(
        [FromQuery] Guid? containerId,
        [FromQuery] Guid? truckId,
        [FromQuery] decimal? customGrossWeightTon,
        CancellationToken cancellationToken)
    {
        var result = await _service.EvaluatePayloadAsync(containerId, truckId, customGrossWeightTon, cancellationToken);
        return Ok(result);
    }

    [Authorize(Roles = "Administrator,Admin,Dispatcher")]
    [HttpPost("{id:guid}/approve")]
    [ProducesResponseType(typeof(BookingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BookingDto>> Approve(
        Guid id,
        CancellationToken cancellationToken)
    {
        var approvedBy = _currentUser.UserId ?? Guid.Empty;
        var item = await _service.ApproveAsync(id, approvedBy, cancellationToken);
        return Ok(item);
    }

    [Authorize(Roles = "Administrator,Admin,Dispatcher")]
    [HttpPost("{id:guid}/reject")]
    [ProducesResponseType(typeof(BookingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BookingDto>> Reject(
        Guid id,
        [FromBody] CancelBookingDto? dto,
        CancellationToken cancellationToken)
    {
        var reason = dto?.Reason ?? "Dispatcher rejected";
        var item = await _service.RejectAsync(id, reason, cancellationToken);
        return Ok(item);
    }


    /// <summary>
    /// NXP-049: Gán/Điều phối Tài xế, Xe đầu kéo, Container cho Booking và chuyển sang trạng thái Ready
    /// </summary>
    [Authorize(Roles = "Administrator,Admin,Dispatcher,Carrier,Transport Company,Carrier Staff")]
    [HttpPost("{id:guid}/assign")]
    [ProducesResponseType(typeof(BookingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BookingDto>> AssignResources(
        Guid id,
        [FromBody] AssignBookingResourcesDto dto,
        CancellationToken cancellationToken)
    {
        Guid? userCarrierId = IsCarrierRole() ? GetUserCarrierId() : null;
        var item = await _service.AssignResourcesAsync(id, dto, userCarrierId, cancellationToken);
        return Ok(item);
    }

    /// <summary>
    /// Lấy chi tiết thông tin cước phí & thanh toán VietQR cho Booking
    /// </summary>
    [Authorize(Roles = "Administrator,Admin,Dispatcher,Carrier,Transport Company,Carrier Staff")]
    [HttpGet("{id:guid}/payment")]
    [ProducesResponseType(typeof(BookingPaymentInfoDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BookingPaymentInfoDto>> GetPaymentInfo(
        Guid id,
        CancellationToken cancellationToken)
    {
        await _service.GetByIdAsync(id, IsCarrierRole() ? GetUserCarrierId() : null, cancellationToken);
        var result = await _service.GetPaymentInfoAsync(id, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Thực hiện / Xác nhận thanh toán cước cảng qua VietQR Napas 24/7
    /// </summary>
    [Authorize(Roles = "Administrator,Admin,Dispatcher,Carrier,Transport Company,Carrier Staff")]
    [HttpPost("{id:guid}/payment")]
    [ProducesResponseType(typeof(BookingPaymentInfoDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BookingPaymentInfoDto>> ProcessPayment(
        Guid id,
        [FromBody] ProcessBookingPaymentDto dto,
        CancellationToken cancellationToken)
    {
        await _service.GetByIdAsync(id, IsCarrierRole() ? GetUserCarrierId() : null, cancellationToken);
        var result = await _service.ProcessPaymentAsync(id, dto, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Lấy danh sách các tác nghiệp của Booking (Export, Import)
    /// </summary>
    [Authorize(Roles = "Administrator,Admin,Dispatcher,Carrier,Transport Company,Carrier Staff,Gate Officer,Gate")]
    [HttpGet("{id:guid}/operations")]
    [ProducesResponseType(typeof(IReadOnlyList<BookingOperationDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<BookingOperationDto>>> GetOperations(
        Guid id,
        CancellationToken cancellationToken)
    {
        var result = await _service.GetOperationsAsync(id, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Cập nhật trạng thái tác nghiệp (Approved, OnHold, InProgress, Completed, Rejected)
    /// </summary>
    [Authorize(Roles = "Administrator,Admin,Dispatcher,Gate Officer,Gate")]
    [HttpPut("{id:guid}/operations/{operationId:guid}/status")]
    [ProducesResponseType(typeof(BookingOperationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BookingOperationDto>> UpdateOperationStatus(
        Guid id,
        Guid operationId,
        [FromBody] UpdateBookingOperationStatusDto dto,
        CancellationToken cancellationToken)
    {
        var result = await _service.UpdateOperationStatusAsync(id, operationId, dto, _currentUser.UserId, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Thêm chứng từ / tài liệu cho Booking hoặc Tác nghiệp (eDO, B/L, Hải quan, VGM, Lệnh cấp vỏ, v.v.)
    /// </summary>
    [Authorize(Roles = "Administrator,Admin,Dispatcher,Carrier,Transport Company,Carrier Staff")]
    [HttpPost("{id:guid}/documents")]
    [ProducesResponseType(typeof(BookingDocumentDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BookingDocumentDto>> AddDocument(
        Guid id,
        [FromBody] CreateBookingDocumentDto dto,
        CancellationToken cancellationToken)
    {
        var result = await _service.AddDocumentAsync(id, dto, cancellationToken);
        return StatusCode(StatusCodes.Status201Created, result);
    }

    /// <summary>
    /// Thẩm định / Duyệt chứng từ đính kèm (Verified hoặc Rejected)
    /// </summary>
    [Authorize(Roles = "Administrator,Admin,Dispatcher,Gate Officer,Gate")]
    [HttpPost("{id:guid}/documents/{documentId:guid}/verify")]
    [ProducesResponseType(typeof(BookingDocumentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BookingDocumentDto>> VerifyDocument(
        Guid id,
        Guid documentId,
        [FromBody] VerifyBookingDocumentDto dto,
        CancellationToken cancellationToken)
    {
        var verifiedBy = _currentUser.UserId ?? Guid.Empty;
        var result = await _service.VerifyDocumentAsync(id, documentId, dto, verifiedBy, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Tra cứu danh mục chứng từ bắt buộc / khuyến nghị theo từng loại tác nghiệp & phân loại hàng
    /// </summary>
    [AllowAnonymous]
    [HttpGet("operation-requirements")]
    [ProducesResponseType(typeof(IReadOnlyList<NexusPort.Modules.Booking.Application.Services.DocumentRequirementSpec>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<NexusPort.Modules.Booking.Application.Services.DocumentRequirementSpec>>> GetDocumentRequirements(
        [FromQuery] string operationType,
        [FromQuery] string? cargoStatus,
        [FromQuery] string? cargoType,
        CancellationToken cancellationToken)
    {
        var result = await _service.GetDocumentRequirementsAsync(operationType, cargoStatus ?? "Loaded", cargoType ?? "General", cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Kiểm tra tính tương thích của tổ hợp tác nghiệp kết hợp (VD: Trả rỗng + Bốc rỗng)
    /// </summary>
    [AllowAnonymous]
    [HttpPost("validate-combination")]
    [ProducesResponseType(typeof(NexusPort.Modules.Booking.Application.Services.OperationCombinationValidationResult), StatusCodes.Status200OK)]
    public async Task<ActionResult<NexusPort.Modules.Booking.Application.Services.OperationCombinationValidationResult>> ValidateCombination(
        [FromBody] System.Text.Json.JsonElement body,
        CancellationToken cancellationToken)
    {
        List<CreateBookingOperationDto>? operations = null;
        var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        if (body.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            operations = System.Text.Json.JsonSerializer.Deserialize<List<CreateBookingOperationDto>>(body.GetRawText(), options);
        }
        else if (body.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            if (body.TryGetProperty("operations", out var opProp) || body.TryGetProperty("Operations", out opProp))
            {
                operations = System.Text.Json.JsonSerializer.Deserialize<List<CreateBookingOperationDto>>(opProp.GetRawText(), options);
            }
        }

        var result = await _service.ValidateCombinationAsync(operations ?? new List<CreateBookingOperationDto>(), cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Tải lên tệp chứng từ đính kèm trực tiếp từ máy tính (AWS S3 hoặc lưu trữ cục bộ)
    /// </summary>
    [AllowAnonymous]
    [HttpPost("upload-document")]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> UploadDocument([FromForm] IFormFile file, CancellationToken cancellationToken)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest(new { message = "Vui lòng chọn tệp chứng từ từ máy tính để tải lên." });
        }

        // Limit size: 25MB
        if (file.Length > 25 * 1024 * 1024)
        {
            return BadRequest(new { message = "Dung lượng tệp không được vượt quá 25MB." });
        }

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        var safeFileName = $"doc_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid().ToString("N")[..8]}{ext}";

        // 1. Thử tải lên AWS S3
        try
        {
            if (_s3StorageService != null)
            {
                using var stream = file.OpenReadStream();
                var s3Url = await _s3StorageService.UploadFileAsync(stream, safeFileName, file.ContentType, "documents", cancellationToken);
                if (!string.IsNullOrEmpty(s3Url))
                {
                    return Ok(new
                    {
                        fileUrl = s3Url,
                        fileName = file.FileName,
                        fileSizeBytes = file.Length,
                        contentType = file.ContentType
                    });
                }
            }
        }
        catch
        {
            // Fallback khi AWS S3 không khả dụng hoặc chưa cấu hình AccessKey
        }

        // 2. Dự phòng lưu trữ cục bộ (Local fallback in wwwroot/uploads/documents)
        try
        {
            var webRoot = _env.WebRootPath;
            if (string.IsNullOrEmpty(webRoot))
            {
                webRoot = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
            }

            var uploadsDir = Path.Combine(webRoot, "uploads", "documents");
            if (!Directory.Exists(uploadsDir))
            {
                Directory.CreateDirectory(uploadsDir);
            }

            var filePath = Path.Combine(uploadsDir, safeFileName);
            using (var localStream = new FileStream(filePath, FileMode.Create))
            {
                await file.CopyToAsync(localStream, cancellationToken);
            }

            var localUrl = $"/uploads/documents/{safeFileName}";
            return Ok(new
            {
                fileUrl = localUrl,
                fileName = file.FileName,
                fileSizeBytes = file.Length,
                contentType = file.ContentType
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = $"Lỗi khi lưu trữ tệp đính kèm: {ex.Message}" });
        }
    }

    private bool IsCarrierRole()
    {
        var role = _currentUser.Role;
        return string.Equals(role, "Carrier", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(role, "Transport Company", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(role, "Carrier Staff", StringComparison.OrdinalIgnoreCase);
    }

    private Guid? GetUserCarrierId()
    {
        if (!_currentUser.UserId.HasValue) throw new UnauthorizedException("Vui lòng đăng nhập.");
        var memberships = _context.Database.SqlQueryRaw<Guid>(
            "SELECT carrier_id AS \"Value\" FROM carrier_users WHERE user_id={0}", _currentUser.UserId.Value).ToList();
        if (memberships.Count != 1) throw new UnauthorizedException("Tài khoản cần thuộc đúng một công ty vận tải.");
        return memberships[0];
    }
}
