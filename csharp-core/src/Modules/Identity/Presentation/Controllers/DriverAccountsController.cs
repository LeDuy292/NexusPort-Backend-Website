using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using NexusPort.Infrastructure.Authentication;
using NexusPort.Infrastructure.Database;
using NexusPort.Modules.Identity.Application.DTOs;
using NexusPort.Modules.Identity.Application.Services;
using NexusPort.Shared.Exceptions;

namespace NexusPort.Modules.Identity.Presentation.Controllers;

[ApiController]
[Route("api/v1/driver-accounts")]
public sealed class DriverAccountsController(DriverAccountService service, ICurrentUser current, AppDbContext db) : ControllerBase
{
    private async Task<Guid> ActorAsync(CancellationToken ct)
    {
        var id = current.UserId ?? throw new UnauthorizedException();
        var session = User.FindFirst("sessionId")?.Value ?? "";
        var valid = await db.Database.SqlQueryRaw<bool>("""
            SELECT EXISTS(SELECT 1 FROM users WHERE id={0} AND is_active AND role IN ('Transport Company','Administrator')
              AND (session_id IS NULL OR session_id={1})) AS "Value"
            """, id, session).SingleAsync(ct);
        if (!valid) throw new UnauthorizedException("Phiên đăng nhập đã kết thúc.");
        return id;
    }

    [Authorize(Roles = "Transport Company")]
    [HttpPost]
    public async Task<IActionResult> Create(DriverAccountDto input, CancellationToken ct)
        => StatusCode(201, await service.CreateAsync(await ActorAsync(ct), input, ct));

    [Authorize(Roles = "Transport Company,Administrator")]
    [HttpPost("{driverId:guid}/resend")]
    public async Task<IActionResult> Resend(Guid driverId, CancellationToken ct)
        => Ok(await service.ResendAsync(await ActorAsync(ct), driverId, User.IsInRole("Administrator"), ct));

    [AllowAnonymous]
    [EnableRateLimiting("account-activation")]
    [HttpPost("activate")]
    public async Task<IActionResult> Activate(ActivateDriverAccountDto input, CancellationToken ct)
        => Ok(await service.ActivateAsync(input, ct));
}
