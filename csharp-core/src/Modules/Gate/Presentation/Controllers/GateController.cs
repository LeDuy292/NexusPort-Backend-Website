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

    public GateController(IGateService service, IGateVerificationService verificationService)
    {
        _service = service;
        _verificationService = verificationService;
    }

    /// <summary>
    /// API tiếp nhận sự kiện nhận diện (Gate Recognition Event) từ AI Camera / YOLO / Cảm biến RFID và đối chiếu điều kiện qua Rule Engine
    /// </summary>
    [HttpPost("verify")]
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
}
