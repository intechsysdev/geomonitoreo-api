using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Geomonitoreo.API.Configuration;
using Geomonitoreo.Application.Common.Interfaces;
using Geomonitoreo.Domain.Entities;
using Geomonitoreo.Infrastructure.Persistence;

namespace Geomonitoreo.API.Controllers;

public record VinculoDto(
    int EmpresaId, Guid OneTenantId, string OneSlug, string Nombre,
    bool OneConfigurado, bool Activo, int Geocercas, DateTime FechaCreacion);

public record VinculoRequest(Guid OneTenantId, string? OneSlug, string? Nombre, string? OneApiKey, string? OneApiSecret);

/// <summary>Lo que One responde para este vínculo, sin secretos: sirve para comprobarlo.</summary>
public record ComprobacionDto(
    bool Alcanzable, string? TenantNombre, string? TenantSlug,
    bool MobiControlConfigurado, string? MobiControlBaseUrl, string? ConfigVersion);

/// <summary>
/// Vínculos entre Geomonitoreo y los tenants de One. Las empresas se crean y se configuran en One;
/// aquí solo se registra con qué credencial preguntarle a One la configuración de cada una. Las
/// filas aparecen solas al primer ingreso de alguien de la empresa.
/// </summary>
[ApiController]
[Route("api/v1/vinculos")]
[Authorize(Roles = One.RolPlataforma)]
public class VinculosController(ApplicationDbContext db, IProveedorConfiguracion configuracion) : ControllerBase
{
    private static VinculoDto AVista(Empresa e, int geocercas) => new(
        e.EmpresaId, e.OneTenantId, e.OneSlug, e.Nombre, e.OneConfigurado, e.Activo, geocercas,
        DateTime.SpecifyKind(e.FechaCreacion, DateTimeKind.Utc));

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<VinculoDto>>> Listar(CancellationToken ct)
    {
        var vinculos = await db.Empresas.AsNoTracking().OrderBy(e => e.Nombre).ToListAsync(ct);

        var geocercas = await db.Geocercas.IgnoreQueryFilters()
            .GroupBy(g => g.EmpresaId)
            .Select(g => new { EmpresaId = g.Key, Total = g.Count() })
            .ToDictionaryAsync(x => x.EmpresaId, x => x.Total, ct);

        return Ok(vinculos.Select(e => AVista(e, geocercas.GetValueOrDefault(e.EmpresaId))).ToList());
    }

    /// <summary>Carga o cambia la credencial de One de la empresa (y, si hace falta, su tenant).</summary>
    [HttpPut("{empresaId:int}")]
    public async Task<ActionResult<VinculoDto>> Actualizar(int empresaId, [FromBody] VinculoRequest solicitud, CancellationToken ct)
    {
        var vinculo = await db.Empresas.FirstOrDefaultAsync(e => e.EmpresaId == empresaId, ct);
        if (vinculo is null) return NotFound(new { message = "No existe ese vínculo." });

        if (solicitud.OneTenantId != Guid.Empty && solicitud.OneTenantId != vinculo.OneTenantId)
        {
            if (await db.Empresas.AnyAsync(e => e.OneTenantId == solicitud.OneTenantId && e.EmpresaId != empresaId, ct))
                return BadRequest(new { message = "Ese tenant ya está vinculado a otra empresa." });

            vinculo.OneTenantId = solicitud.OneTenantId;
        }

        if (!string.IsNullOrWhiteSpace(solicitud.Nombre)) vinculo.Nombre = solicitud.Nombre.Trim();
        if (!string.IsNullOrWhiteSpace(solicitud.OneSlug)) vinculo.OneSlug = solicitud.OneSlug.Trim();
        if (!string.IsNullOrWhiteSpace(solicitud.OneApiKey)) vinculo.OneApiKey = solicitud.OneApiKey.Trim();

        // El secreto no se devuelve nunca: si llegara vacío y se escribiera, editar el nombre
        // dejaría al vínculo sin poder consultar su configuración.
        if (!string.IsNullOrWhiteSpace(solicitud.OneApiSecret)) vinculo.OneApiSecret = solicitud.OneApiSecret.Trim();

        vinculo.FechaActualizacion = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        configuracion.Olvidar(empresaId);

        var geocercas = await db.Geocercas.IgnoreQueryFilters().CountAsync(g => g.EmpresaId == empresaId, ct);
        return Ok(AVista(vinculo, geocercas));
    }

    /// <summary>Comprueba el vínculo contra One y devuelve lo que responde, sin secretos.</summary>
    [HttpGet("{empresaId:int}/comprobacion")]
    public async Task<ActionResult<ComprobacionDto>> Comprobar(int empresaId, CancellationToken ct)
    {
        if (!await db.Empresas.AnyAsync(e => e.EmpresaId == empresaId, ct))
            return NotFound(new { message = "No existe ese vínculo." });

        configuracion.Olvidar(empresaId);
        var config = await configuracion.ObtenerAsync(empresaId, ct);

        return Ok(config is null
            ? new ComprobacionDto(false, null, null, false, null, null)
            : new ComprobacionDto(true, config.TenantNombre, config.TenantSlug,
                config.MobiControlConfigurado, config.MobiControlBaseUrl, config.ConfigVersion));
    }

    /// <summary>Activa o desactiva el vínculo. No se borra: sus geocercas se conservan.</summary>
    [HttpPost("{empresaId:int}/activo")]
    public async Task<IActionResult> CambiarEstado(int empresaId, [FromBody] bool activo, CancellationToken ct)
    {
        var vinculo = await db.Empresas.FirstOrDefaultAsync(e => e.EmpresaId == empresaId, ct);
        if (vinculo is null) return NotFound(new { message = "No existe ese vínculo." });

        vinculo.Activo = activo;
        vinculo.FechaActualizacion = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return Ok(new { message = activo ? "Vínculo activado." : "Vínculo desactivado." });
    }
}
