using System.Text;
using System.Text.Json;
using RabbitMQ.Client;

namespace NexusPort.Infrastructure.ExternalServices;

public interface IMessageBrokerService
{
    Task PublishAsync<T>(string queueName, T message, CancellationToken cancellationToken = default);
}

public class MessageBrokerService : IMessageBrokerService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<MessageBrokerService> _logger;

    public MessageBrokerService(IConfiguration configuration, ILogger<MessageBrokerService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public Task PublishAsync<T>(string queueName, T message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var factory = new ConnectionFactory
            {
                HostName = _configuration["RabbitMQ:Host"] ?? "localhost",
                Port = int.TryParse(_configuration["RabbitMQ:Port"], out var port) ? port : AmqpTcpEndpoint.UseDefaultPort,
                UserName = _configuration["RabbitMQ:Username"] ?? "guest",
                Password = _configuration["RabbitMQ:Password"] ?? "guest",
                DispatchConsumersAsync = true
            };

            using var connection = factory.CreateConnection();
            using var channel = connection.CreateModel();
            channel.QueueDeclare(queue: queueName, durable: true, exclusive: false, autoDelete: false);

            var properties = channel.CreateBasicProperties();
            properties.Persistent = true;
            properties.ContentType = "application/json";
            properties.Type = queueName;
            var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));
            channel.BasicPublish(exchange: string.Empty, routingKey: queueName, basicProperties: properties, body: body);
            _logger.LogInformation("Published integration event to {QueueName}", queueName);
            return Task.CompletedTask;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to publish integration event to {QueueName}", queueName);
            throw;
        }
    }
}

public interface IEmailService
{
    Task SendEmailAsync(string to, string subject, string body, CancellationToken cancellationToken = default);
}

public class EmailService : IEmailService
{
    public Task SendEmailAsync(string to, string subject, string body, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
