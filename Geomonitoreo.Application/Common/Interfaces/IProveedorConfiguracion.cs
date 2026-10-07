namespace Geomonitoreo.Application.Common.Interfaces;

/// <summary>
/// Configuración de una empresa, resuelta desde One. Las claves son el contrato con el catálogo
/// de apps: cambiar un nombre aquí obliga a cambiarlo en el esquema de la app en One.
/// </summary>
public sealed record ConfiguracionEmpresa(
    string TenantSlug,
    string TenantNombre,
    string? MobiControlBaseUrl,
    string? MobiControlClientId,
    string? MobiControlClientSecret,
    string? MobiControlUsuario,
    string? MobiControlPassword,
    int MobiControlTimeoutSegundos,
    string ConfigVersion,
    string? GoogleMapsApiKey = null,
    string? GoogleMapsMapId = null)
{
    public bool MobiControlConfigurado =>
        !string.IsNullOrWhiteSpace(MobiControlBaseUrl) &&
        !string.IsNullOrWhiteSpace(MobiControlClientId) &&
        !string.IsNullOrWhiteSpace(MobiControlClientSecret) &&
        !string.IsNullOrWhiteSpace(MobiControlUsuario) &&
        !string.IsNullOrWhiteSpace(MobiControlPassword);
}

/// <summary>Resuelve la configuración de una empresa preguntándole a One.</summary>
public interface IProveedorConfiguracion
{
    /// <summary>Null cuando la empresa no tiene credencial de One o cuando One la rechaza.</summary>
    Task<ConfiguracionEmpresa?> ObtenerAsync(int empresaId, CancellationToken ct = default);

    /// <summary>Descarta lo cacheado de una empresa. Se usa tras cambiar su credencial.</summary>
    void Olvidar(int empresaId);
}
