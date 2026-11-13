using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using System.Text;
using NexusPort.Infrastructure.Authentication;
using NexusPort.Infrastructure.Database;
using NexusPort.Infrastructure.ExternalServices;
using NexusPort.Modules.Booking.Application.Interfaces;
using NexusPort.Modules.Booking.Application.Services;
using NexusPort.Modules.Booking.Infrastructure.Repositories;
using NexusPort.Modules.Identity.Application.Interfaces;
using NexusPort.Modules.Identity.Application.Services;
using NexusPort.Modules.Identity.Infrastructure.Repositories;
using NexusPort.Modules.Vessel.Application.Interfaces;
using NexusPort.Modules.Vessel.Application.Services;
using NexusPort.Modules.Vessel.Infrastructure.Repositories;
using NexusPort.Modules.Berth.Application.Interfaces;
using NexusPort.Modules.Berth.Application.Services;
using NexusPort.Modules.Berth.Infrastructure.Repositories;
using NexusPort.Modules.Container.Application.Interfaces;
using NexusPort.Modules.Container.Application.Services;
using NexusPort.Modules.Container.Infrastructure.Repositories;
using NexusPort.Modules.Yard.Application.Interfaces;
using NexusPort.Modules.Yard.Application.Services;
using NexusPort.Modules.Yard.Infrastructure.Repositories;
using NexusPort.Modules.Gate.Application.Interfaces;
using NexusPort.Modules.Gate.Application.Services;
using NexusPort.Modules.Gate.Domain.Rules;
using NexusPort.Modules.Gate.Domain.Rules.Concrete;
using NexusPort.Modules.Gate.Infrastructure.Repositories;
using NexusPort.Modules.Dispatcher.Application.Interfaces;
using NexusPort.Modules.Dispatcher.Application.Services;
using NexusPort.Modules.Dispatcher.Infrastructure.Repositories;
using NexusPort.Modules.Vehicle.Application.Interfaces;
using NexusPort.Modules.Vehicle.Application.Services;
using NexusPort.Modules.Vehicle.Infrastructure.Repositories;
using NexusPort.Modules.Driver.Application.Interfaces;
using NexusPort.Modules.Driver.Application.Services;
using NexusPort.Modules.Driver.Infrastructure.Repositories;
using NexusPort.Modules.Equipment.Application.Interfaces;
using NexusPort.Modules.Equipment.Application.Services;
using NexusPort.Modules.Equipment.Infrastructure.Repositories;

using NexusPort.Infrastructure.Notifications.Interfaces;
using NexusPort.Infrastructure.Notifications.Services;

namespace NexusPort.Api.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNexusPortInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection") 
            ?? "Host=localhost;Port=5432;Database=NexusPort;Username=postgres;Password=pgadmin4";

        var dataSourceBuilder = new Npgsql.NpgsqlDataSourceBuilder(connectionString);
        dataSourceBuilder.MapEnum<NexusPort.Modules.Driver.Domain.Enums.DriverStatus>("driver_status");
        dataSourceBuilder.MapEnum<NexusPort.Modules.Vehicle.Domain.Enums.TruckStatus>("truck_status");
        dataSourceBuilder.MapEnum<NexusPort.Modules.Booking.Domain.Enums.BookingType>("booking_type");
        dataSourceBuilder.MapEnum<NexusPort.Modules.Booking.Domain.Enums.BookingStatus>("booking_status");
        var nullTranslator = new Npgsql.NameTranslation.NpgsqlNullNameTranslator();
        dataSourceBuilder.MapEnum<NexusPort.Modules.Yard.Domain.Enums.ReeferRequestStatus>("reefer_request_status", nullTranslator);
        dataSourceBuilder.MapEnum<NexusPort.Modules.Yard.Domain.Enums.SocketUsageStatus>("socket_usage_status", nullTranslator);
        dataSourceBuilder.MapEnum<NexusPort.Modules.Yard.Domain.Enums.SocketTechnicalStatus>("socket_technical_status", nullTranslator);
        dataSourceBuilder.MapEnum<NexusPort.Modules.Yard.Domain.Enums.PowerSessionStatus>("power_session_status", nullTranslator);
        dataSourceBuilder.MapEnum<NexusPort.Modules.Yard.Domain.Enums.ActualPowerStatus>("actual_power_status", nullTranslator);
        dataSourceBuilder.MapEnum<NexusPort.Modules.Yard.Domain.Enums.TempDataSource>("temp_data_source", nullTranslator);
        dataSourceBuilder.MapEnum<NexusPort.Modules.Yard.Domain.Enums.AlertSeverity>("alert_severity", nullTranslator);
        dataSourceBuilder.MapEnum<NexusPort.Modules.Yard.Domain.Enums.ReeferAlertStatus>("reefer_alert_status", nullTranslator);

        dataSourceBuilder.MapEnum<NexusPort.Modules.Container.Domain.Enums.ContainerStorageStatus>("container_status");
        var dataSource = dataSourceBuilder.Build();

        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(dataSource));

        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, CurrentUser>();
        services.AddScoped<ExportBookingService>();

        var jwtSecret = configuration["Jwt:Secret"] ?? "NexusPort_Super_Secret_Key_For_Jwt_Authentication_2026!";
        services.AddSingleton<IJwtService>(new JwtService(jwtSecret));

        services.AddScoped<IMessageBrokerService, MessageBrokerService>();
        services.AddScoped<IEmailService, EmailService>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<IS3StorageService, S3StorageService>();

        services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        })
        .AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
                ValidateIssuer = false,
                ValidateAudience = false,
                ClockSkew = TimeSpan.Zero
            };
            options.Events = new JwtBearerEvents
            {
                OnTokenValidated = async context =>
                {
                    var principal = context.Principal;
                    var role = principal?.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value ?? principal?.FindFirst("role")?.Value;
                    if (!string.Equals(role, "Driver", StringComparison.OrdinalIgnoreCase)) return;
                    var id = principal?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                        ?? principal?.FindFirst("sub")?.Value ?? principal?.FindFirst("id")?.Value;
                    var session = principal?.FindFirst("sessionId")?.Value;
                    if (!Guid.TryParse(id, out var userId) || string.IsNullOrWhiteSpace(session))
                    {
                        context.Fail("Driver account must log in after email verification.");
                        return;
                    }
                    var db = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
                    var connection = db.Database.GetDbConnection();
                    var opened = connection.State != System.Data.ConnectionState.Open;
                    try
                    {
                        if (opened) await connection.OpenAsync(context.HttpContext.RequestAborted);
                        await using var command = connection.CreateCommand();
                        command.CommandText = "SELECT EXISTS(SELECT 1 FROM users WHERE id=@id AND role='Driver' AND is_active AND email_verified_at IS NOT NULL AND NOT invitation_pending AND session_id=@session)";
                        var idParameter = command.CreateParameter(); idParameter.ParameterName = "id"; idParameter.Value = userId; command.Parameters.Add(idParameter);
                        var sessionParameter = command.CreateParameter(); sessionParameter.ParameterName = "session"; sessionParameter.Value = session; command.Parameters.Add(sessionParameter);
                        if (await command.ExecuteScalarAsync(context.HttpContext.RequestAborted) is not true)
                            context.Fail("Driver email is not verified or session has ended.");
                    }
                    finally { if (opened) await connection.CloseAsync(); }
                },
                OnMessageReceived = context =>
                {
                    var accessToken = context.Request.Query["access_token"];
                    var path = context.HttpContext.Request.Path;
                    if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
                    {
                        context.Token = accessToken;
                    }
                    return Task.CompletedTask;
                }
            };
        });

        services.AddCors(options =>
        {
            options.AddPolicy("NexusPortCorsPolicy", policy =>
            {
                policy.SetIsOriginAllowed(_ => true)
                      .AllowAnyMethod()
                      .AllowAnyHeader()
                      .AllowCredentials();
            });
        });

        return services;
    }

    public static IServiceCollection AddNexusPortModules(this IServiceCollection services)
    {
        // Register Module Configuration Assemblies into EF Core DbContext
        AppDbContext.ModuleAssemblies.Add(typeof(NexusPort.Modules.Identity.Infrastructure.Configurations.UserConfiguration).Assembly);
        AppDbContext.ModuleAssemblies.Add(typeof(NexusPort.Modules.Booking.Infrastructure.Configurations.BookingConfiguration).Assembly);
        AppDbContext.ModuleAssemblies.Add(typeof(NexusPort.Modules.Vessel.Infrastructure.Configurations.VesselConfiguration).Assembly);
        AppDbContext.ModuleAssemblies.Add(typeof(NexusPort.Modules.Berth.Infrastructure.Configurations.BerthConfiguration).Assembly);
        AppDbContext.ModuleAssemblies.Add(typeof(NexusPort.Modules.Container.Infrastructure.Configurations.ContainerConfiguration).Assembly);
        AppDbContext.ModuleAssemblies.Add(typeof(NexusPort.Modules.Yard.Infrastructure.Configurations.YardBlockConfiguration).Assembly);
        AppDbContext.ModuleAssemblies.Add(typeof(NexusPort.Modules.Gate.Infrastructure.Configurations.GateTransactionConfiguration).Assembly);
        AppDbContext.ModuleAssemblies.Add(typeof(NexusPort.Modules.Dispatcher.Infrastructure.Configurations.WorkOrderConfiguration).Assembly);
        AppDbContext.ModuleAssemblies.Add(typeof(NexusPort.Modules.Vehicle.Infrastructure.Configurations.VehicleConfiguration).Assembly);
        AppDbContext.ModuleAssemblies.Add(typeof(NexusPort.Modules.Driver.Infrastructure.Configurations.DriverConfiguration).Assembly);
        AppDbContext.ModuleAssemblies.Add(typeof(NexusPort.Modules.Equipment.Infrastructure.Configurations.EquipmentConfiguration).Assembly);
        AppDbContext.ModuleAssemblies.Add(typeof(NexusPort.Infrastructure.Notifications.Configurations.NotificationConfiguration).Assembly);

        // Identity
        services.AddScoped<IIdentityRepository, IdentityRepository>();
        services.AddScoped<IIdentityService, IdentityService>();

        // Booking
        services.AddScoped<IBookingRepository, BookingRepository>();
        services.AddScoped<IBookingCompatibilityPolicy, BookingCompatibilityPolicy>();
        services.AddScoped<IBookingDocumentRequirementPolicy, BookingDocumentRequirementPolicy>();
        services.AddScoped<IBookingValidationService, BookingValidationService>();
        services.AddScoped<IBookingService, BookingService>();

        // Vessel
        services.AddScoped<IVesselRepository, VesselRepository>();
        services.AddScoped<IVesselService, VesselService>();

        // Berth
        services.AddScoped<IBerthRepository, BerthRepository>();
        services.AddScoped<IBerthService, BerthService>();

        // Container
        services.AddScoped<IContainerRepository, ContainerRepository>();
        services.AddScoped<IContainerService, ContainerService>();

        // Yard
        services.AddScoped<IYardRepository, YardRepository>();
        services.AddScoped<IYardService, YardService>();
        services.AddScoped<IYardReceivingService, YardReceivingService>();
        services.AddScoped<IYardTaskService, YardTaskService>();
        services.AddScoped<IYardRestackService, YardRestackService>();
        services.AddScoped<NexusPort.Modules.Yard.Application.Interfaces.IReeferMonitoringService, NexusPort.Modules.Yard.Application.Services.ReeferMonitoringService>();
        services.AddHostedService<NexusPort.Modules.Yard.Infrastructure.BackgroundServices.ReeferMonitoringSimulatorWorker>();

        // Gate
        services.AddScoped<IGateRepository, GateRepository>();
        services.AddScoped<IGateService, GateService>();
        services.AddScoped<IGateVerificationRepository, GateVerificationRepository>();
        services.AddScoped<IGateVerificationService, GateVerificationService>();
        services.AddHttpClient("ContainerOcr", client => client.Timeout = TimeSpan.FromSeconds(12));
        services.AddSingleton<IContainerDetectionService, NexusPort.Modules.Gate.Infrastructure.Services.ContainerOnnxDetectionService>();
        services.AddScoped<IGateContainerInspectionService, GateContainerInspectionService>();

        // Gate Rule Engine & Rules (Extensible Pipeline)
        services.AddScoped<IGateRuleEngine, GateRuleEngine>();
        services.AddScoped<IGateRule, BookingExistenceAndStatusRule>();
        services.AddScoped<IGateRule, GateEntryOrderRule>();
        services.AddScoped<IGateRule, BookingTimeWindowRule>();
        services.AddScoped<IGateRule, VehicleMatchAndStatusRule>();
        services.AddScoped<IGateRule, DriverMatchAndStatusRule>();
        services.AddScoped<IGateRule, ContainerMatchAndStatusRule>();
        services.AddScoped<IGateRule, OperationMatchRule>();
        services.AddScoped<IGateRule, DriverConfirmationRule>();
        services.AddScoped<IGateRule, BillingAndPaymentStatusRule>();

        // Dispatcher
        services.AddScoped<IDispatcherRepository, DispatcherRepository>();
        services.AddScoped<IDispatcherService, DispatcherService>();

        // Vehicle
        services.AddScoped<IVehicleRepository, VehicleRepository>();
        services.AddScoped<IVehicleService, VehicleService>();

        // Driver
        services.AddScoped<IDriverRepository, DriverRepository>();
        services.AddScoped<IDriverService, DriverService>();
        services.AddHttpClient<IOcrService, OcrService>();

        // Equipment
        services.AddScoped<IEquipmentRepository, EquipmentRepository>();
        services.AddScoped<IEquipmentService, EquipmentService>();

        return services;
    }

    public static IServiceCollection AddSwaggerDocumentation(this IServiceCollection services)
    {
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "NexusPort API (.NET 8)",
                Version = "v1",
                Description = "Hệ điều hành Cảng biển NexusPort Core Backend API"
            });

            c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
            {
                Description = "JWT Authorization header using the Bearer scheme. Example: \"Authorization: Bearer {token}\"",
                Name = "Authorization",
                In = ParameterLocation.Header,
                Type = SecuritySchemeType.ApiKey,
                Scheme = "Bearer"
            });

            c.AddSecurityRequirement(new OpenApiSecurityRequirement
            {
                {
                    new OpenApiSecurityScheme
                    {
                        Reference = new OpenApiReference
                        {
                            Type = ReferenceType.SecurityScheme,
                            Id = "Bearer"
                        }
                    },
                    Array.Empty<string>()
                }
            });
        });

        return services;
    }
}
