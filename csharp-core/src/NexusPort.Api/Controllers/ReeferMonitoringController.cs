using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NexusPort.Infrastructure.Authentication;
using NexusPort.Modules.Yard.Application.DTOs.Reefer;
using NexusPort.Modules.Yard.Application.Interfaces;

namespace NexusPort.Api.Controllers;

[ApiController]
[Route("api/v1/reefer/monitoring")]
[Produces("application/json")]
public class ReeferMonitoringController : ControllerBase
{
    private readonly IReeferMonitoringService _monitoringService;
    private readonly ICurrentUser _currentUser;
    private readonly ILogger<ReeferMonitoringController> _logger;

    public ReeferMonitoringController(
        IReeferMonitoringService monitoringService,
        ICurrentUser currentUser,
        ILogger<ReeferMonitoringController> logger)
    {
        _monitoringService = monitoringService;
        _currentUser = currentUser;
        _logger = logger;
    }

    /// <summary>
    /// Lấy tổng quan trạng thái giám sát ổ cắm, phiên cấp điện và cảnh báo nhiệt độ
    /// </summary>
    [HttpGet]
    [HttpGet("overview")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetOverview(CancellationToken cancellationToken)
    {
        var overview = await _monitoringService.GetMonitoringOverviewAsync(cancellationToken);
        return Ok(new { success = true, data = overview });
    }

    /// <summary>
    /// Lấy lịch sử đo đạc nhiệt độ bất biến của một phiên cấp điện container
    /// </summary>
    [HttpGet("sessions/{sessionId:guid}/history")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSessionHistory(
        Guid sessionId,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var history = await _monitoringService.GetSessionTemperatureHistoryAsync(sessionId, limit, cancellationToken);
        return Ok(new { success = true, data = history });
    }

    /// <summary>
    /// Lấy danh sách các cảnh báo an toàn nhiệt độ và mất nguồn
    /// </summary>
    [HttpGet("alerts")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAlerts(
        [FromQuery] string? status = null,
        [FromQuery] string? severity = null,
        CancellationToken cancellationToken = default)
    {
        var alerts = await _monitoringService.GetAlertsAsync(status, severity, cancellationToken);
        return Ok(new { success = true, data = alerts });
    }

    /// <summary>
    /// Operator tiếp nhận xử lý cảnh báo
    /// </summary>
    [HttpPost("alerts/{id:guid}/acknowledge")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AcknowledgeAlert(
        Guid id,
        [FromBody] AcknowledgeAlertDto? dto,
        CancellationToken cancellationToken)
    {
        var userId = _currentUser.UserId ?? Guid.Parse("30768160-2766-47ab-aa88-d0187cd47c5d");
        var result = await _monitoringService.AcknowledgeAlertAsync(id, userId, dto?.Notes, cancellationToken);
        if (result == null)
            return NotFound(new { success = false, message = "Không tìm thấy cảnh báo yêu cầu." });

        return Ok(new { success = true, data = result });
    }

    /// <summary>
    /// Ghi nhận khắc phục hoàn tất cảnh báo
    /// </summary>
    [HttpPost("alerts/{id:guid}/resolve")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ResolveAlert(
        Guid id,
        [FromBody] AcknowledgeAlertDto dto,
        CancellationToken cancellationToken)
    {
        var userId = _currentUser.UserId ?? Guid.Parse("30768160-2766-47ab-aa88-d0187cd47c5d");
        var result = await _monitoringService.ResolveAlertAsync(id, userId, dto.Notes ?? "Đã xử lý khắc phục.", cancellationToken);
        if (result == null)
            return NotFound(new { success = false, message = "Không tìm thấy cảnh báo yêu cầu." });

        return Ok(new { success = true, data = result });
    }

    /// <summary>
    /// Kích hoạt kịch bản mô phỏng kiểm thử (NORMAL, EXCURSION, POWER_LOSS, RECOVERY)
    /// </summary>
    [HttpPost("simulator/scenario")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public async Task<IActionResult> SetDemoScenario(
        [FromBody] SimulateScenarioRequest request,
        CancellationToken cancellationToken)
    {
        await _monitoringService.SetDemoScenarioAsync(request, cancellationToken);
        return Ok(new
        {
            success = true,
            message = $"Đã thiết lập kịch bản mô phỏng: {request.Scenario}",
            scenario = request.Scenario
        });
    }

    /// <summary>
    /// Kích hoạt chu kỳ mô phỏng thủ công ngay lập tức
    /// </summary>
    [HttpPost("simulator/tick")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public async Task<IActionResult> TriggerSimulationTick(CancellationToken cancellationToken)
    {
        await _monitoringService.RunSimulationCycleAsync(cancellationToken);
        return Ok(new { success = true, message = "Đã thực thi 1 chu kỳ đo đạc mô phỏng thành công." });
    }
}
