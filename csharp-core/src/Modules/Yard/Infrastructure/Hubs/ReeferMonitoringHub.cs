using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using NexusPort.Modules.Yard.Application.Interfaces;

namespace NexusPort.Modules.Yard.Infrastructure.Hubs;

public class ReeferMonitoringHub : Hub<IReeferMonitoringHubClient>
{
    private readonly ILogger<ReeferMonitoringHub> _logger;

    public ReeferMonitoringHub(ILogger<ReeferMonitoringHub> logger)
    {
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("Client connected to ReeferMonitoringHub: ConnectionId={ConnectionId}, User={User}",
            Context.ConnectionId, Context.User?.Identity?.Name ?? "Anonymous");

        // Auto-join overview group
        await Groups.AddToGroupAsync(Context.ConnectionId, "overview");
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("Client disconnected from ReeferMonitoringHub: ConnectionId={ConnectionId}", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    public async Task SubscribeToOverview()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "overview");
    }

    public async Task UnsubscribeFromOverview()
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, "overview");
    }

    public async Task SubscribeToSession(string sessionId)
    {
        if (Guid.TryParse(sessionId, out _))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"session_{sessionId.ToLowerInvariant()}");
            _logger.LogDebug("Client {ConnectionId} subscribed to session_{SessionId}", Context.ConnectionId, sessionId);
        }
    }

    public async Task UnsubscribeFromSession(string sessionId)
    {
        if (Guid.TryParse(sessionId, out _))
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"session_{sessionId.ToLowerInvariant()}");
        }
    }
}
