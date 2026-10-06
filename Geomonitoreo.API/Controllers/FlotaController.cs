using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Geomonitoreo.Application.Flota;

namespace Geomonitoreo.API.Controllers;

/// <summary>La flota de la empresa elegida: estado, última posición y zonas de cada equipo.</summary>
[ApiController]
[Route("api/v1/flota")]
[Authorize]
public class FlotaController(IServicioFlota flota) : ControllerBase
{
    /// <summary>Si la empresa tiene su consola de MobiControl configurada en One.</summary>
    [HttpGet("configuracion")]
    public async Task<ActionResult<ConfiguracionDto>> Configuracion(CancellationToken ct) =>
        Ok(await flota.ConfiguracionAsync(ct));

    /// <summary>
    /// Todos los equipos. La respuesta puede ser de hace unos segundos (ver <c>consultado</c>);
    /// con <c>refrescar=true</c> se le vuelve a preguntar a MobiControl.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<FlotaDto>> Listar([FromQuery] bool refrescar, CancellationToken ct) =>
        Ok(await flota.ListarAsync(refrescar, ct));
}
