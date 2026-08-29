using NexusPort.Api.Extensions;
using NexusPort.Api.Middleware;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers()
    .AddApplicationPart(typeof(NexusPort.Modules.Identity.Presentation.Controllers.IdentityController).Assembly)
    .AddApplicationPart(typeof(NexusPort.Modules.Booking.Presentation.Controllers.BookingController).Assembly)
    .AddApplicationPart(typeof(NexusPort.Modules.Vessel.Presentation.Controllers.VesselController).Assembly)
    .AddApplicationPart(typeof(NexusPort.Modules.Berth.Presentation.Controllers.BerthController).Assembly)
    .AddApplicationPart(typeof(NexusPort.Modules.Container.Presentation.Controllers.ContainerController).Assembly)
    .AddApplicationPart(typeof(NexusPort.Modules.Yard.Presentation.Controllers.YardController).Assembly)
    .AddApplicationPart(typeof(NexusPort.Modules.Gate.Presentation.Controllers.GateController).Assembly)
    .AddApplicationPart(typeof(NexusPort.Modules.Dispatcher.Presentation.Controllers.DispatcherController).Assembly)
    .AddApplicationPart(typeof(NexusPort.Modules.Vehicle.Presentation.Controllers.VehicleController).Assembly)
    .AddApplicationPart(typeof(NexusPort.Modules.Driver.Presentation.Controllers.DriverController).Assembly)
    .AddApplicationPart(typeof(NexusPort.Modules.Equipment.Presentation.Controllers.EquipmentController).Assembly);

// Add Infrastructure & Domain Modules
builder.Services.AddNexusPortInfrastructure(builder.Configuration);
builder.Services.AddNexusPortModules();
builder.Services.AddSwaggerDocumentation();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "NexusPort API v1 (.NET 8)");
        c.RoutePrefix = string.Empty; // Swagger at root /
    });
}

app.UseMiddleware<GlobalExceptionMiddleware>();
app.UseMiddleware<RequestLoggingMiddleware>();

app.UseCors("NexusPortCorsPolicy");

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
