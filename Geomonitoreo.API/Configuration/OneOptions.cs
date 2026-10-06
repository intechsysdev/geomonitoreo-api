namespace Geomonitoreo.API.Configuration;

/// <summary>
/// Conexión con One, el centralizador de empresas y configuración.
///
/// Este API no emite credenciales ni verifica firmas: delega en One tanto la autenticación de
/// los usuarios como la configuración de cada empresa.
/// </summary>
public class OneOptions
{
    public const string SectionName = "One";

    /// <summary>Raíz del API de One, para consultar la configuración de cada empresa.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Slug de esta app en el catálogo de One. Con él se pregunta a qué empresas llega cada usuario.</summary>
    public string AppSlug { get; set; } = "geomonitoreo";

    /// <summary>
    /// No hay llave de firma a propósito: este API no verifica tokens, se los pasa a One para
    /// que los valide. Así el secreto de la plataforma no tiene que viajar hasta aquí.
    /// </summary>
    public bool EstaConfigurado => !string.IsNullOrWhiteSpace(BaseUrl);
}

/// <summary>Claims y cabeceras del protocolo de One que este API entiende.</summary>
public static class One
{
    /// <summary>Pertenencia a una empresa, con formato "{tenantId}:{rol}".</summary>
    public const string ClaimTenant = "tenant";

    /// <summary>Rol de plataforma que ve todas las empresas.</summary>
    public const string RolPlataforma = "PlatformAdmin";

    /// <summary>
    /// Empresa sobre la que trabaja la petición. Un usuario puede pertenecer a varias, así que
    /// la consola indica cuál tiene abierta; sin cabecera se usa la única que tenga.
    /// </summary>
    public const string CabeceraTenant = "X-Tenant-Id";
}
