using Microsoft.AspNetCore.Mvc;
using NexusPort.Modules.Yard.Application.DTOs;
using NexusPort.Modules.Yard.Application.Interfaces;
using System.Security.Claims;

namespace NexusPort.Modules.Yard.Presentation.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class YardRestackController : ControllerBase
{
    private readonly IYardRestackService _restackService;

    public YardRestackController(IYardRestackService restackService)
    {
        _restackService = restackService;
    }

    /// <summary>
    /// NXP-126: Phân tích Stack, phát hiện container bị chặn và tìm slot đệm
    /// </summary>
    [HttpPost("analyze")]
    public async Task<ActionResult<AnalyzeBlockedContainerResponseDto>> Analyze(
        [FromBody] AnalyzeBlockedContainerRequestDto dto,
        CancellationToken cancellationToken)
    {
        var result = await _restackService.AnalyzeBlockedContainerAsync(dto, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// NXP-126: Lập bản thảo kế hoạch di dời container chồng
    /// </summary>
    [HttpPost("plan")]
    public async Task<ActionResult<RestackPlanDto>> CreatePlan(
        [FromBody] CreateRestackPlanDto dto,
        CancellationToken cancellationToken)
    {
        var createdBy = User.FindFirstValue(ClaimTypes.Name) ?? User.FindFirstValue("username") ?? "Yard Staff";
        var plan = await _restackService.CreatePlanAsync(dto, createdBy, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = plan.Id }, plan);
    }

    /// <summary>
    /// NXP-126: Lấy chi tiết kế hoạch theo ID
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<RestackPlanDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var plan = await _restackService.GetPlanByIdAsync(id, cancellationToken);
        if (plan == null) return NotFound(new { message = "Không tìm thấy kế hoạch đảo bãi" });
        return Ok(plan);
    }

    /// <summary>
    /// NXP-126: Lấy toàn bộ danh sách lịch sử và kế hoạch đảo bãi
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<RestackPlanDto>>> GetAll(
        [FromQuery] string? status,
        CancellationToken cancellationToken)
    {
        var plans = await _restackService.GetAllPlansAsync(status, cancellationToken);
        return Ok(plans);
    }

    /// <summary>
    /// NXP-126: Triển khai kế hoạch & Phát chuỗi lệnh cẩu RTG xuống bãi
    /// </summary>
    [HttpPost("{id:guid}/deploy")]
    public async Task<ActionResult<DeployRestackPlanResponseDto>> Deploy(
        Guid id,
        CancellationToken cancellationToken)
    {
        var deployedBy = User.FindFirstValue(ClaimTypes.Name) ?? User.FindFirstValue("username") ?? "Điều Phối Viên Bãi";
        var result = await _restackService.DeployPlanAsync(id, deployedBy, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// NXP-126: Xác nhận hoàn thành một bước cẩu và tự động chuyển bước tiếp theo
    /// </summary>
    [HttpPost("{id:guid}/steps/{stepNumber:int}/complete")]
    public async Task<ActionResult<RestackPlanDto>> CompleteStep(
        Guid id,
        int stepNumber,
        [FromBody] AdvanceRestackStepDto? dto,
        CancellationToken cancellationToken)
    {
        var operatorName = User.FindFirstValue(ClaimTypes.Name) ?? User.FindFirstValue("username") ?? "Trần Văn Hùng (RTG-01)";
        var updatedPlan = await _restackService.CompleteStepAsync(id, stepNumber, dto ?? new AdvanceRestackStepDto(), operatorName, cancellationToken);
        return Ok(updatedPlan);
    }

    /// <summary>
    /// NXP-126: Hủy kế hoạch đảo bãi
    /// </summary>
    [HttpPost("{id:guid}/cancel")]
    public async Task<ActionResult<RestackPlanDto>> Cancel(
        Guid id,
        [FromBody] AdvanceRestackStepDto? dto,
        CancellationToken cancellationToken)
    {
        var reason = dto?.Notes ?? "Điều phối viên hủy thủ công";
        var plan = await _restackService.CancelPlanAsync(id, reason, cancellationToken);
        return Ok(plan);
    }

    [HttpPost("seed")]
    public async Task<ActionResult> Seed(CancellationToken cancellationToken)
    {
        await _restackService.EnsureSeedPlansAsync(cancellationToken);
        return Ok(new { message = "Khởi tạo dữ liệu mẫu Kế hoạch đảo bãi thành công!" });
    }
}
