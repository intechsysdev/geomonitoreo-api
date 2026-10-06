using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Geomonitoreo.API.Configuration;
using Geomonitoreo.Application.Dispositivos;

namespace Geomonitoreo.API.Controllers;

/// <summary>Un equipo en detalle, su recorrido y la acción de pedirle la ubicación.</summary>
[ApiController]
[Route("api/v1/dispositivos")]
[Authorize]
public class DispositivosController(IServicioDispositivos dispositivos) : ControllerBase
{
    /// <summary>Colombia no tiene horario de verano: el día es siempre de -05:00 a -05:00.</summary>
    private static readonly TimeZoneInfo Zona = TimeZoneInfo.FindSystemTimeZoneById("America/Bogota");

    [HttpGet("{deviceId}")]
    public async Task<ActionResult<DetalleDispositivoDto>> Obtener(string deviceId, CancellationToken ct) =>
        Ok(await dispositivos.ObtenerAsync(deviceId, ct));

    /// <summary>
    /// Puntos, paradas, estadísticas y entradas o salidas de geocercas entre dos momentos (hasta
    /// 7 días). Sin rango, el día de hoy en hora de Colombia.
    /// </summary>
    [HttpGet("{deviceId}/recorrido")]
    public async Task<ActionResult<RecorridoDto>> Recorrido(
        string deviceId, [FromQuery] DateTimeOffset? desde, [FromQuery] DateTimeOffset? hasta, CancellationToken ct)
    {
        var ahora = DateTimeOffset.UtcNow;
        var hoy = TimeZoneInfo.ConvertTime(ahora, Zona).Date;
        var inicioHoy = new DateTimeOffset(hoy, Zona.GetUtcOffset(hoy));

        return Ok(await dispositivos.RecorridoAsync(deviceId, desde ?? inicioHoy, hasta ?? ahora, ct));
    }

    /// <summary>
    /// Pide al equipo que reporte su posición. Despierta el teléfono y gasta batería, así que solo
    /// se hace cuando alguien lo pide, nunca al consultar la flota.
    /// </summary>
    [HttpPost("{deviceId}/localizar")]
    [EnableRateLimiting(PoliticasLimite.Acciones)]
    public async Task<IActionResult> Localizar(string deviceId, CancellationToken ct)
    {
        await dispositivos.LocalizarAsync(deviceId, ct);
        return Accepted(new { message = "Se le pidió al equipo su ubicación. Llega en unos segundos si está en línea." });
    }
}
