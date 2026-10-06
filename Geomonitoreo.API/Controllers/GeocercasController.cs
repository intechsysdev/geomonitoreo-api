using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Geomonitoreo.Application.Geocercas;

namespace Geomonitoreo.API.Controllers;

/// <summary>
/// Geocercas de la empresa. Viven en MobiControl: crear, editar y borrar aquí lo hace en la consola,
/// y la lista trae la forma vigente de cada una desde allá.
/// </summary>
[ApiController]
[Route("api/v1/geocercas")]
[Authorize]
public class GeocercasController(IServicioGeocercas geocercas) : ControllerBase
{
    /// <summary>
    /// Las geocercas que conoce Geomonitoreo, actualizadas desde MobiControl. Si la consola no
    /// responde, salen las formas guardadas con <c>sincronizadas: false</c> y el motivo.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<ListaGeocercasDto>> Listar(CancellationToken ct) =>
        Ok(await geocercas.ListarAsync(ct));

    [HttpPost]
    public async Task<ActionResult<GeocercaDto>> Crear(GuardarGeocercaRequest solicitud, CancellationToken ct)
    {
        var creada = await geocercas.CrearAsync(solicitud, ct);
        return CreatedAtAction(nameof(Listar), null, creada);
    }

    /// <summary>
    /// Trae una geocerca hecha en la consola de MobiControl. Va por nombre porque la API de
    /// MobiControl no permite listarlas.
    /// </summary>
    [HttpPost("importar")]
    public async Task<ActionResult<GeocercaDto>> Importar(ImportarGeocercaRequest solicitud, CancellationToken ct) =>
        Ok(await geocercas.ImportarAsync(solicitud.Nombre, ct));

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
