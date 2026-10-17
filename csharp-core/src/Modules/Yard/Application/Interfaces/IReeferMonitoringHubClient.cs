namespace NexusPort.Modules.Yard.Application.Interfaces;

public interface IReeferMonitoringHubClient
{
    Task MonitoringUpdated(object payload);
    Task ReeferAlertCreated(object payload);
    Task ReeferAlertUpdated(object payload);
}
