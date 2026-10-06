namespace Geomonitoreo.API.Configuration;

/// <summary>Ajustes generales del API.</summary>
public class AppOptions
{
    public const string SectionName = "App";

    /// <summary>Orígenes permitidos por CORS: el front de la consola. Vacío, se permite cualquiera.</summary>
    public string[] CorsOrigins { get; set; } = [];
}
