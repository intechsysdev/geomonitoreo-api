using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Geomonitoreo.Application.Geocercas;

namespace Geomonitoreo.API.Controllers;

/// <summary>Zonas de la empresa contra las que se compara la posición de los equipos.</summary>
[ApiController]
[Route("api/v1/geocercas")]
[Authorize]
public class GeocercasController(IServicioGeocercas geocercas) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<GeocercaDto>>> Listar(CancellationToken ct) =>
        Ok(await geocercas.ListarAsync(ct));

    [HttpPost]
    public async Task<ActionResult<GeocercaDto>> Crear(GuardarGeocercaRequest solicitud, CancellationToken ct)
    {
        var creada = await geocercas.CrearAsync(solicitud, ct);
        return CreatedAtAction(nameof(Listar), null, creada);
    }

    [HttpPut("{uid:guid}")]
    public async Task<ActionResult<GeocercaDto>> Actualizar(Guid uid, GuardarGeocercaRequest solicitud, CancellationToken ct) =>
        Ok(await geocercas.ActualizarAsync(uid, solicitud, ct));

    [HttpDelete("{uid:guid}")]
    public async Task<IActionResult> Eliminar(Guid uid, CancellationToken ct)
    {
        await geocercas.EliminarAsync(uid, ct);
        return NoContent();
    }
}
