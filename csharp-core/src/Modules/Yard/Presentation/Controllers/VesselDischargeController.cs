using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using NexusPort.Modules.Yard.Application.DTOs;
using NexusPort.Modules.Yard.Application.Interfaces;

namespace NexusPort.Modules.Yard.Presentation.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class VesselDischargeController : ControllerBase
{
    private readonly IYardDischargeService _dischargeService;

    public VesselDischargeController(IYardDischargeService dischargeService)
    {
        _dischargeService = dischargeService;
    }

    /// <summary>
    /// Kích hoạt thuật toán phân bổ ô bãi thông minh (Pre-allocation) cho chuyến tàu từ Manifest
    /// </summary>
    [HttpPost("pre-allocate/{manifestId:guid}")]
    public async Task<ActionResult<VesselDischargePlanDto>> PreAllocateManifest(
        Guid manifestId,
        CancellationToken cancellationToken)
    {
        var plan = await _dischargeService.PreAllocateManifestAsync(manifestId, cancellationToken);
        return Ok(plan);
    }

    /// <summary>
    /// Lấy danh sách các kế hoạch dỡ tàu (chiều nhập) đang hiệu lực cho Điều độ cảng
    /// </summary>
    [HttpGet("plans")]
    public async Task<ActionResult<IReadOnlyList<VesselDischargePlanDto>>> GetActivePlans(
        CancellationToken cancellationToken)
    {
        var plans = await _dischargeService.GetActiveDischargePlansAsync(cancellationToken);
        return Ok(plans);
    }

    /// <summary>
    /// Lấy chi tiết kế hoạch dỡ tàu theo ManifestId
    /// </summary>
    [HttpGet("plan/{manifestId:guid}")]
    public async Task<ActionResult<VesselDischargePlanDto>> GetPlanById(
        Guid manifestId,
        CancellationToken cancellationToken)
    {
        var plan = await _dischargeService.GetDischargePlanByIdAsync(manifestId, cancellationToken);
        if (plan == null) return NotFound(new { message = $"Không tìm thấy kế hoạch dỡ cho Manifest {manifestId}." });
        return Ok(plan);
    }

    /// <summary>
    /// Tally quét container tại chân cẩu bờ: đối chiếu manifest, nhận diện ô bãi đã đặt trước,
    /// ghi nhận tình trạng vỏ (EIR) và mốc giờ dỡ hàng.
    /// </summary>
    [HttpPost("tally-scan")]
    public async Task<ActionResult<TallyScanResultDto>> TallyScan(
        [FromBody] TallyScanRequestDto dto,
        CancellationToken cancellationToken)
    {
        var inspectorName = User?.Identity?.Name ?? "Tallyman / Dispatcher";
        var result = await _dischargeService.TallyScanAsync(dto, inspectorName, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Cho phép Dispatcher điều chỉnh thủ công ô bãi dự kiến cho một container
    /// </summary>
    [HttpPost("override-slot")]
    public async Task<ActionResult<VesselDischargePlanDto>> OverrideSlot(
        [FromBody] OverrideSlotRequestDto dto,
        CancellationToken cancellationToken)
    {
        var plan = await _dischargeService.OverrideSlotAsync(dto, cancellationToken);
        return Ok(plan);
    }
}
