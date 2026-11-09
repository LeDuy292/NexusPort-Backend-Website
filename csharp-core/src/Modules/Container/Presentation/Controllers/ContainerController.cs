using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NexusPort.Infrastructure.Database;
namespace NexusPort.Modules.Container.Presentation.Controllers;

[Authorize(Roles = "Administrator,Admin,Dispatcher,Carrier,Transport Company,Carrier Staff,Yard Staff,Yard Operator,Gate Officer")]
[ApiController]
[Route("api/v1/[controller]")]
public class ContainerController : ControllerBase
{
    private readonly AppDbContext _context;
    public ContainerController(AppDbContext context) => _context = context;
    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] string? containerNumber, CancellationToken cancellationToken)
    {
        var prefix = containerNumber?.Trim().ToUpperInvariant() ?? "";
        if (!System.Text.RegularExpressions.Regex.IsMatch(prefix, "^[A-Z0-9]{4,11}$"))
            return BadRequest(new { message = "Nhập số container trước khi tìm kiếm." });
        return Ok(await _context.Database.SqlQueryRaw<SharedContainerIdentity>(
            "SELECT c.id AS \"Id\",c.container_no AS \"ContainerNumber\",ct.code AS \"ContainerTypeCode\" FROM containers c JOIN container_types ct ON ct.id=c.container_type_id WHERE c.container_no LIKE {0} ORDER BY c.container_no LIMIT 50", prefix + "%").ToListAsync(cancellationToken));
    }
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken)
    {
        var item = await _context.Database.SqlQueryRaw<SharedContainerIdentity>(
            "SELECT c.id AS \"Id\",c.container_no AS \"ContainerNumber\",ct.code AS \"ContainerTypeCode\" FROM containers c JOIN container_types ct ON ct.id=c.container_type_id WHERE c.id={0}",id).FirstOrDefaultAsync(cancellationToken);
        return item == null ? NotFound() : Ok(item);
    }
    [HttpPost]
    public IActionResult Create() => StatusCode(410,new { message = "Khai báo container qua API Node có xác thực và bản khai riêng." });
}
public class SharedContainerIdentity
{
    public Guid Id { get; set; }
    public string ContainerNumber { get; set; } = string.Empty;
    public string ContainerTypeCode { get; set; } = string.Empty;
}
