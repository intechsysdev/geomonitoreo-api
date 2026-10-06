namespace Geomonitoreo.Application.Common.Interfaces;

/// <summary>
/// Cliente de la API de MobiControl de la empresa de la petición. Todas las operaciones lanzan
/// ErrorSolicitudException con un mensaje para el usuario cuando la empresa no tiene consola
/// configurada o MobiControl no responde: quien mira el mapa necesita saber por qué no ve nada,
/// no una lista vacía.
/// </summary>
public interface IClienteMobiControl
{
    /// <summary>False cuando la empresa no tiene su consola configurada en One.</summary>
    Task<bool> EstaConfiguradoAsync(CancellationToken ct = default);

    /// <summary>Todos los equipos de la consola, con su estado.</summary>
    Task<IReadOnlyList<EquipoMobiControl>> ListarEquiposAsync(CancellationToken ct = default);

    /// <summary>Un equipo, o null si la consola no lo tiene.</summary>
    Task<EquipoMobiControl?> ObtenerEquipoAsync(string deviceId, CancellationToken ct = default);

    /// <summary>Última posición conocida, o null si el equipo nunca reportó una.</summary>
    Task<UbicacionMobiControl?> UltimaUbicacionAsync(string deviceId, CancellationToken ct = default);

    /// <summary>Puntos GPS recolectados entre dos momentos, en orden cronológico.</summary>
    Task<IReadOnlyList<UbicacionMobiControl>> RecorridoAsync(
        string deviceId, DateTimeOffset desde, DateTimeOffset hasta, CancellationToken ct = default);

    /// <summary>Le pide al equipo que reporte su posición ahora. La respuesta llega después, no aquí.</summary>
    Task LocalizarAsync(string deviceId, CancellationToken ct = default);
}

/// <summary>Un equipo tal como lo ve MobiControl, reducido a lo que se muestra.</summary>
public record EquipoMobiControl(
    string DeviceId,
    string Nombre,
    string? Plataforma,
    string? Fabricante,
    string? Modelo,
    bool EnLinea,
    int? Bateria,
    bool? Cargando,
    DateTimeOffset? UltimoReporte,
    string? Grupo,
    string? Imei,
    string? Telefono,
    string? Serial,
    string? VersionSistema = null,
    string? VersionAgente = null,
    DateTimeOffset? FechaInscripcion = null,
    string? Red = null)
{
    /// <summary>Solo teléfonos y tabletas reportan posición: pedirla a un Mac o a un PC es una llamada perdida.</summary>
    public bool TieneGps => Plataforma is "Android" or "iOS";
}

public record UbicacionMobiControl(
    double Latitud, double Longitud, DateTimeOffset Momento, double? Velocidad, double? Rumbo);
