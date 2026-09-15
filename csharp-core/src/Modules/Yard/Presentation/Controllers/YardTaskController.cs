using Microsoft.AspNetCore.Mvc;
using NexusPort.Modules.Yard.Application.DTOs;
using NexusPort.Modules.Yard.Application.Interfaces;
using System.Security.Claims;

namespace NexusPort.Modules.Yard.Presentation.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class YardTaskController : ControllerBase
{
    private readonly IYardTaskService _taskService;

    public YardTaskController(IYardTaskService taskService)
    {
        _taskService = taskService;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<YardTaskDto>>> GetAll(
        [FromQuery] string? block,
        [FromQuery] string? status,
        CancellationToken cancellationToken)
    {
        var tasks = await _taskService.GetAllAsync(block, status, cancellationToken);
        return Ok(tasks);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<YardTaskDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var task = await _taskService.GetByIdAsync(id, cancellationToken);
        if (task == null) return NotFound(new { message = "Không tìm thấy nhiệm vụ tác nghiệp bãi" });
        return Ok(task);
    }

    [HttpPost("{id:guid}/assign-equipment")]
    public async Task<ActionResult<YardTaskDto>> AssignEquipment(
        Guid id,
        [FromBody] AssignEquipmentRequestDto dto,
        CancellationToken cancellationToken)
    {
        var assignedBy = User.FindFirstValue(ClaimTypes.Name) ?? User.FindFirstValue("username") ?? "Yard Staff";
        var result = await _taskService.AssignEquipmentAsync(id, dto, assignedBy, cancellationToken);
        return Ok(new
        {
            success = true,
            message = $"Gán thiết bị {result.EquipmentCode} và cần thủ {result.OperatorName} thành công!",
            data = result
        });
    }

    /// <summary>NXP-060: Bắt đầu cẩu container (Ready -> In_Progress)</summary>
    [HttpPost("{id:guid}/start-lift")]
    public async Task<ActionResult<YardTaskDto>> StartLift(
        Guid id,
        [FromBody] StartLiftDto dto,
        CancellationToken cancellationToken)
    {
        var operatorName = User.FindFirstValue(ClaimTypes.Name) ?? User.FindFirstValue("username") ?? "Yard Operator";
        var result = await _taskService.StartLiftAsync(id, dto, operatorName, cancellationToken);
        return Ok(new
        {
            success = true,
            message = $"Bắt đầu cẩu container {result.ContainerNo} thành công!",
            data = result
        });
    }

    /// <summary>NXP-060: Hoàn thành cẩu container (In_Progress -> Completed)</summary>
    [HttpPost("{id:guid}/complete-lift")]
    public async Task<ActionResult<YardTaskDto>> CompleteLift(
        Guid id,
        [FromBody] CompleteLiftDto dto,
        CancellationToken cancellationToken)
    {
        var operatorName = User.FindFirstValue(ClaimTypes.Name) ?? User.FindFirstValue("username") ?? "Yard Operator";
        var result = await _taskService.CompleteLiftAsync(id, dto, operatorName, cancellationToken);
        return Ok(new
        {
            success = true,
            message = $"Hoàn thành cẩu container {result.ContainerNo}, hạ bãi an toàn tại {result.CompletedLocation}!",
            data = result
        });
    }

    /// <summary>NXP-055: Tiếp nhận & Kiểm tra đối soát container tại bãi</summary>
    [HttpPost("receiving-inspect")]
    public async Task<ActionResult<YardTaskDto>> ReceiveContainerAtYard(
        [FromBody] YardReceivingInspectionDto dto,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.Name) ?? User.FindFirstValue("username") ?? "Yard Staff";
        var result = await _taskService.ReceiveContainerAtYardAsync(dto, userId, cancellationToken);
        return Ok(new
        {
            success = true,
            message = $"Tiếp nhận & kiểm tra đối soát container {result.ContainerNo} thành công!",
            data = result
        });
    }

    [HttpGet("operators/available")]
    public async Task<ActionResult<IReadOnlyList<YardOperatorDto>>> GetAvailableOperators(CancellationToken cancellationToken)
    {
        var operators = await _taskService.GetAvailableOperatorsAsync(cancellationToken);
        return Ok(operators);
    }

    [HttpPost]
    public async Task<ActionResult<YardTaskDto>> CreateTask(
        [FromBody] CreateYardTaskDto dto,
        CancellationToken cancellationToken)
    {
        var result = await _taskService.CreateTaskAsync(dto, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = result.Id }, result);
    }

    [HttpPost("seed")]
    public async Task<ActionResult> SeedTasks(CancellationToken cancellationToken)
    {
        await _taskService.EnsureSeedTasksAsync(cancellationToken);
        return Ok(new { message = "Khởi tạo dữ liệu mẫu Yard Tasks thành công!" });
    }
}
