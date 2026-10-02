using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using NexusPort.Modules.Carrier.Application.Services;
using NexusPort.Modules.Carrier.Infrastructure.Persistence;

namespace NexusPort.Modules.Carrier;

public static class CarrierModuleExtensions
{
    public static IServiceCollection AddCarrierModule(this IServiceCollection services, string connectionString)
    {
        var dataSourceBuilder = new Npgsql.NpgsqlDataSourceBuilder(connectionString);
        dataSourceBuilder.MapEnum<NexusPort.Modules.Carrier.Domain.Enums.CompanyStatus>("company_status");
        var dataSource = dataSourceBuilder.Build();

        services.AddDbContext<CarrierDbContext>(options =>
            options.UseNpgsql(dataSource));

        services.AddScoped<ICarrierService, CarrierService>();
        
        return services;
    }
}
