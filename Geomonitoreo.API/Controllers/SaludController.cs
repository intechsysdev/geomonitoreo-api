using Microsoft.AspNetCore.Mvc;
using Geomonitoreo.Infrastructure.Persistence;

namespace Geomonitoreo.API.Controllers;

/// <summary>Comprobación de vida, sin sesión: la usan el despliegue y el monitoreo del App Service.</summary>
[ApiController]
[Route("api/v1/salud")]
public class SaludController(ApplicationDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Estado(CancellationToken ct)
    {
        var baseDatos = await db.Database.CanConnectAsync(ct);
        return Ok(new { estado = baseDatos ? "ok" : "degradado", baseDatos, utc = DateTime.UtcNow });
    }
}
