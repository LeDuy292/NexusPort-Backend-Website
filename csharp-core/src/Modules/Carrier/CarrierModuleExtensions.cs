using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using NexusPort.Modules.Carrier.Application.Services;
using NexusPort.Modules.Carrier.Infrastructure.Persistence;

namespace NexusPort.Modules.Carrier;

public static class CarrierModuleExtensions
{
    public static IServiceCollection AddCarrierModule(this IServiceCollection services, string connectionString)
    {
        var options = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
        if (!string.Equals(options.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(options.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase))
        {
            options.MinPoolSize = 0;
            options.MaxPoolSize = Math.Max(5, options.MaxPoolSize);
            options.ConnectionIdleLifetime = 60;
            options.ConnectionPruningInterval = 10;
            connectionString = options.ConnectionString;
        }
        var dataSourceBuilder = new Npgsql.NpgsqlDataSourceBuilder(connectionString);
        dataSourceBuilder.MapEnum<NexusPort.Modules.Carrier.Domain.Enums.CompanyStatus>("company_status");
        var dataSource = dataSourceBuilder.Build();

        services.AddDbContext<CarrierDbContext>(options =>
            options.UseNpgsql(dataSource));

        services.AddScoped<ICarrierService, CarrierService>();
        
        return services;
    }
}
