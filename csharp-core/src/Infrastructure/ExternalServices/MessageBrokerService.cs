namespace NexusPort.Infrastructure.ExternalServices;

public interface IMessageBrokerService
{
    Task PublishAsync<T>(string queueName, T message, CancellationToken cancellationToken = default);
}

public class MessageBrokerService : IMessageBrokerService
{
    public Task PublishAsync<T>(string queueName, T message, CancellationToken cancellationToken = default)
    {
        // Placeholder implementation for RabbitMQ publishing
        return Task.CompletedTask;
    }
}

public interface IEmailService
{
    Task SendEmailAsync(string to, string subject, string body, CancellationToken cancellationToken = default);
}

public class EmailService : IEmailService
{
    public Task SendEmailAsync(string to, string subject, string body, CancellationToken cancellationToken = default)
    {
        // Placeholder implementation for email sending
        return Task.CompletedTask;
    }
}
