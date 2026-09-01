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

namespace NexusPort.Api.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNexusPortInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection") 
            ?? "Host=localhost;Port=5432;Database=nexusport;Username=postgres;Password=120104";

        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(connectionString));

        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, CurrentUser>();

        var jwtSecret = configuration["Jwt:Secret"] ?? "NexusPort_Super_Secret_Key_For_Jwt_Authentication_2026!";
        services.AddSingleton<IJwtService>(new JwtService(jwtSecret));

        services.AddScoped<IMessageBrokerService, MessageBrokerService>();
        services.AddScoped<IEmailService, EmailService>();

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
        });

        services.AddCors(options =>
        {
            options.AddPolicy("NexusPortCorsPolicy", policy =>
            {
                policy.AllowAnyOrigin()
                      .AllowAnyMethod()
                      .AllowAnyHeader();
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

        // Identity
        services.AddScoped<IIdentityRepository, IdentityRepository>();
        services.AddScoped<IIdentityService, IdentityService>();

        // Booking
        services.AddScoped<IBookingRepository, BookingRepository>();
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

        // Gate
        services.AddScoped<IGateRepository, GateRepository>();
        services.AddScoped<IGateService, GateService>();

        // Dispatcher
        services.AddScoped<IDispatcherRepository, DispatcherRepository>();
        services.AddScoped<IDispatcherService, DispatcherService>();

        // Vehicle
        services.AddScoped<IVehicleRepository, VehicleRepository>();
        services.AddScoped<IVehicleService, VehicleService>();

        // Driver
        services.AddScoped<IDriverRepository, DriverRepository>();
        services.AddScoped<IDriverService, DriverService>();

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
