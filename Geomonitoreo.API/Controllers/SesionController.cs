using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Geomonitoreo.API.Configuration;
using Geomonitoreo.Domain.Entities;
using Geomonitoreo.Infrastructure.Persistence;

namespace Geomonitoreo.API.Controllers;

public record EmpresaAccesibleDto(int EmpresaId, Guid OneTenantId, string Slug, string Nombre, string? Rol);

public record SesionDto(
    string Correo, string? Nombre, bool EsAdministradorPlataforma,
    IReadOnlyList<EmpresaAccesibleDto> Empresas);

/// <summary>
/// Quién es quien llama y a qué empresas alcanza. La consola lo consulta al entrar.
///
/// La respuesta la da One: las empresas que tienen la app asignada y de las que el usuario es
/// miembro (todas las asignadas, si es de plataforma). Aquí solo se refleja esa lista y se crea
/// la fila local de la empresa que entra por primera vez.
/// </summary>
[ApiController]
[Route("api/v1/sesion")]
[Authorize]
public class SesionController(ApplicationDbContext db, EmpresasOne empresasOne, ILogger<SesionController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<SesionDto>> Yo(CancellationToken ct)
    {
        var esPlataforma = User.IsInRole(One.RolPlataforma);
        var token = Request.Headers.Authorization.ToString()["Bearer ".Length..].Trim();

        var enOne = await empresasOne.ConsultarAsync(token, ct);

        IQueryable<Empresa> consulta;

        if (enOne is not null)
        {
            await empresasOne.SincronizarAsync(enOne, ct);

            var ids = enOne.Select(e => e.TenantId).ToList();
            consulta = db.Empresas.AsNoTracking().Where(e => e.Activo && ids.Contains(e.OneTenantId));
        }
        else
        {
            // Si One no contesta esta consulta, se cae a lo que dice el token: las pertenencias
            // contra los vínculos que ya existen. Así una falla de One no deja a nadie afuera de
            // lo que ya tenía.
            logger.LogWarning("Se usan las pertenencias del token porque One no devolvió las empresas de la app.");

            var pertenencias = ResolucionTenantMiddleware.Pertenencias(User);
            consulta = db.Empresas.AsNoTracking().Where(e => e.Activo);
            if (!esPlataforma) consulta = consulta.Where(e => pertenencias.Contains(e.OneTenantId));
        }

        var empresas = await consulta
            .OrderBy(e => e.Nombre)
            .Select(e => new { e.EmpresaId, e.OneTenantId, e.OneSlug, e.Nombre })
            .ToListAsync(ct);

        var salida = empresas
            .Select(e => new EmpresaAccesibleDto(
                e.EmpresaId, e.OneTenantId, e.OneSlug, e.Nombre,
                ResolucionTenantMiddleware.RolEn(User, e.OneTenantId)))
            .ToList();

        return Ok(new SesionDto(
            User.FindFirstValue(JwtRegisteredClaimNames.Email) ?? User.Identity?.Name ?? "",
            User.FindFirstValue(JwtRegisteredClaimNames.Name),
            esPlataforma,
            salida));
    }
}
