using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace NexusPort.Infrastructure.Authentication;

public interface ICurrentUser
{
    Guid? UserId { get; }
    string? Username { get; }
    string? Email { get; }
    string? Role { get; }
    bool IsAuthenticated { get; }
}

public class CurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CurrentUser(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public Guid? UserId
    {
        get
        {
            var idClaim = _httpContextAccessor.HttpContext?.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? _httpContextAccessor.HttpContext?.User?.FindFirst("sub")?.Value
                ?? _httpContextAccessor.HttpContext?.User?.FindFirst("id")?.Value;
            return Guid.TryParse(idClaim, out var userId) ? userId : null;
        }
    }

    public string? Username => _httpContextAccessor.HttpContext?.User?.FindFirst(ClaimTypes.Name)?.Value
        ?? _httpContextAccessor.HttpContext?.User?.FindFirst("username")?.Value
        ?? _httpContextAccessor.HttpContext?.User?.FindFirst("name")?.Value;

    public string? Email => _httpContextAccessor.HttpContext?.User?.FindFirst(ClaimTypes.Email)?.Value
        ?? _httpContextAccessor.HttpContext?.User?.FindFirst("email")?.Value;

    public string? Role => _httpContextAccessor.HttpContext?.User?.FindFirst(ClaimTypes.Role)?.Value
        ?? _httpContextAccessor.HttpContext?.User?.FindFirst("role")?.Value;

    public bool IsAuthenticated => _httpContextAccessor.HttpContext?.User?.Identity?.IsAuthenticated ?? false;
}
