using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Geomonitoreo.Infrastructure.Persistence;

namespace Geomonitoreo.API.Configuration;

/// <summary>
/// Traduce la identidad que viene de One a la empresa local sobre la que trabaja la petición.
///
/// Va en un middleware y no dentro del contexto de datos porque ese contexto necesita saber por
/// qué empresa filtrar antes de poder consultar nada: si la traducción viviera ahí, se
/// consultaría a sí mismo. Aquí se resuelve una vez, contra la tabla de vínculos —que no está
/// sujeta al filtro— y se deja en la petición.
/// </summary>
public class ResolucionTenantMiddleware(RequestDelegate siguiente)
{
    public async Task InvokeAsync(HttpContext contexto, ApplicationDbContext db)
    {
        if (contexto.User.Identity?.IsAuthenticated == true)
            await ResolverDesdeTokenAsync(contexto, db);

        await siguiente(contexto);
    }

    private static async Task ResolverDesdeTokenAsync(HttpContext contexto, ApplicationDbContext db)
    {
        var esPlataforma = contexto.User.IsInRole(One.RolPlataforma);

        // Un administrador de plataforma ve todas las empresas mientras no elija una.
        if (esPlataforma) contexto.Items[ContextoEmpresa.ClaveSuperAdmin] = true;

        var pertenencias = Pertenencias(contexto.User);
        var solicitado = contexto.Request.Headers[One.CabeceraTenant].FirstOrDefault();

        Guid? elegido = null;

        if (Guid.TryParse(solicitado, out var pedido))
        {
            // Solo se acepta si el token dice que pertenece, o si es de plataforma. Sin esta
            // comprobación bastaría cambiar una cabecera para leer los datos de otra empresa.
            if (esPlataforma || pertenencias.Contains(pedido)) elegido = pedido;
        }
        else if (pertenencias.Count == 1)
        {
            elegido = pertenencias[0];
        }

        if (elegido is not { } tenantId) return;

        var empresaId = await db.Empresas
            .Where(e => e.OneTenantId == tenantId && e.Activo)
            .Select(e => (int?)e.EmpresaId)
            .FirstOrDefaultAsync();

        if (empresaId is null) return;

        contexto.Items[ContextoEmpresa.ClaveEnContexto] = empresaId.Value;

        // Con empresa elegida deja de mirar por encima, aunque sea de plataforma: si no, el
        // filtro global se desactivaría y se verían las geocercas de todas.
        contexto.Items[ContextoEmpresa.ClaveSuperAdmin] = false;
    }

    /// <summary>Empresas del token. One las emite como "{tenantId}:{rol}".</summary>
    public static List<Guid> Pertenencias(ClaimsPrincipal usuario) =>
        usuario.FindAll(One.ClaimTenant)
            .Select(c => c.Value.Split(':')[0])
            .Select(v => Guid.TryParse(v, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .Distinct()
            .ToList();

    /// <summary>Rol del usuario en una empresa concreta, tal como lo emitió One.</summary>
    public static string? RolEn(ClaimsPrincipal usuario, Guid tenantId) =>
        usuario.FindAll(One.ClaimTenant)
            .Select(c => c.Value.Split(':'))
            .Where(p => p.Length == 2 && Guid.TryParse(p[0], out var g) && g == tenantId)
            .Select(p => p[1])
            .FirstOrDefault();
}
