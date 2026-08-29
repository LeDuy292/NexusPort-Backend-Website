using Microsoft.AspNetCore.Mvc;

namespace NexusPort.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public abstract class BaseApiController : ControllerBase
{
}

[ApiController]
[Route("api/[controller]")]
public class HealthController : ControllerBase
{
    [HttpGet]
    public IActionResult Check()
    {
        return Ok(new
        {
            Status = "Healthy",
            Service = "NexusPort C# Core API (.NET 8)",
            Timestamp = DateTime.UtcNow,
            Version = "1.0.0"
        });
    }
}
