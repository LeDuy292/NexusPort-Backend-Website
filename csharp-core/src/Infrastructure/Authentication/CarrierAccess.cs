using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NexusPort.Infrastructure.Database;

namespace NexusPort.Infrastructure.Authentication;

public static class CarrierAccess
{
    // Membership is authoritative; a missing company is an authorization error, not an expired session.
    public static async Task<Guid?> ResolveAsync(AppDbContext db, ClaimsPrincipal user, CancellationToken token)
    {
        var id = user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user.FindFirst("sub")?.Value ?? user.FindFirst("id")?.Value;
        if (!Guid.TryParse(id, out var userId)) return null;
        var memberships = await db.Database.SqlQueryRaw<Guid>(
            "SELECT carrier_id AS \"Value\" FROM carrier_users WHERE user_id={0} LIMIT 2", userId).ToListAsync(token);
        return memberships.Count == 1 ? memberships[0] : null;
    }
}
