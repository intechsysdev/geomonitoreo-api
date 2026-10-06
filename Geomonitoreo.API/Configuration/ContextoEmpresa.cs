using System.Security.Claims;
using Geomonitoreo.Application.Common.Interfaces;

namespace Geomonitoreo.API.Configuration;

/// <summary>
/// Empresa de la petición en curso. La resuelve el middleware de tenant a partir del token de One
/// y la cabecera X-Tenant-Id, y la deja en la petición.
/// </summary>
public class ContextoEmpresa(IHttpContextAccessor acceso) : IContextoEmpresa
{
    /// <summary>Clave con la que el middleware deja la empresa resuelta.</summary>
    public const string ClaveEnContexto = "EmpresaId";

    /// <summary>Clave con la que el middleware decide si la petición ve todas las empresas.</summary>
    public const string ClaveSuperAdmin = "EsSuperAdministrador";

    private HttpContext? Contexto => acceso.HttpContext;

    public int? EmpresaId =>
        Contexto is not null && Contexto.Items.TryGetValue(ClaveEnContexto, out var valor) && valor is int id
            ? id
            : null;

    public bool EsSuperAdministrador
    {
        get
        {
            if (Contexto is null) return false;

            // Si la petición ya trae una decisión tomada, manda esa: un administrador de plataforma
            // que eligió una empresa ve solo esa, aunque su rol le permita verlas todas.
            if (Contexto.Items.TryGetValue(ClaveSuperAdmin, out var valor) && valor is bool decidido)
                return decidido;

            return Contexto.User.IsInRole(One.RolPlataforma);
        }
    }

    public int EmpresaRequerida => EmpresaId
        ?? throw new InvalidOperationException("La petición no tiene empresa asociada.");

    public string? Usuario => Contexto?.User.FindFirstValue(ClaimTypes.Email);
}
