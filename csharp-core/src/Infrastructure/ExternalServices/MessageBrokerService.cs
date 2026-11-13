using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
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
    private readonly IConfiguration _configuration;
    private readonly ILogger<EmailService> _logger;

    public EmailService(IConfiguration configuration, ILogger<EmailService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task SendEmailAsync(string to, string subject, string body, CancellationToken cancellationToken = default)
    {
        try
        {
            var host = _configuration["EmailSettings:SmtpHost"] ?? "smtp.gmail.com";
            var port = int.TryParse(_configuration["EmailSettings:SmtpPort"], out var p) ? p : 587;
            var user = _configuration["EmailSettings:SmtpUser"];
            var pass = _configuration["EmailSettings:SmtpPass"];
            var senderName = _configuration["EmailSettings:SenderName"] ?? "NexusPort";
            var senderEmail = _configuration["EmailSettings:SenderEmail"] ?? user ?? "noreply@nexusport.com";
            var enableSsl = !bool.TryParse(_configuration["EmailSettings:EnableSsl"], out var ssl) || ssl;

            if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
            {
                _logger.LogWarning("EmailSettings not fully configured. Skipping email delivery to {To}", to);
                return;
            }

            using var client = new SmtpClient(host, port)
            {
                Credentials = new NetworkCredential(user, pass),
                EnableSsl = enableSsl
            };

            using var mail = new MailMessage
            {
                From = new MailAddress(senderEmail, senderName),
                Subject = subject,
                Body = body,
                IsBodyHtml = true
            };
            mail.To.Add(to);

            await client.SendMailAsync(mail, cancellationToken);
            _logger.LogInformation("Email sent successfully to {To}", to);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to send email to {To}", to);
            throw;
        }
    }
}
