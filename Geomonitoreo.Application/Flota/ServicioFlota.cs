using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Geomonitoreo.Application.Common;
using Geomonitoreo.Application.Common.Interfaces;
using Geomonitoreo.Domain.Entities;

namespace Geomonitoreo.Application.Flota;

/// <summary>Un equipo en el mapa: su estado en MobiControl, su última posición y en qué zonas está.</summary>
/// <param name="Geocercas">Zonas activas que contienen la última posición.</param>
public record EquipoFlotaDto(
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
    bool TieneGps,
    double? Latitud,
    double? Longitud,
    DateTimeOffset? FechaUbicacion,
    double? Velocidad,
    double? Rumbo,
    IReadOnlyList<Guid> Geocercas);

/// <param name="Consultado">Cuándo se le preguntó a MobiControl; puede ser unos segundos antes que ahora.</param>
public record FlotaDto(IReadOnlyList<EquipoFlotaDto> Equipos, DateTimeOffset Consultado);

public record ConfiguracionDto(bool MobiControlConfigurado, string? Empresa);

public interface IServicioFlota
{
    Task<ConfiguracionDto> ConfiguracionAsync(CancellationToken ct = default);

    /// <param name="refrescar">Ignora la copia reciente y vuelve a preguntarle a MobiControl.</param>
    Task<FlotaDto> ListarAsync(bool refrescar = false, CancellationToken ct = default);
}

/// <summary>
/// La flota de la empresa: MobiControl dice qué equipos hay, cómo están y dónde reportaron por
/// última vez; las geocercas de la empresa dicen en qué zona está cada uno.
/// </summary>
public class ServicioFlota(
    IApplicationDbContext db,
    IContextoEmpresa empresa,
    IProveedorConfiguracion configuracion,
    IClienteMobiControl mobiControl,
    IMemoryCache cache,
    ILogger<ServicioFlota> logger) : IServicioFlota
{
    /// <summary>
    /// Consultas de ubicación simultáneas. La consola deja de responder si se le pide la posición
    /// de toda la flota a la vez: con un tope se reparte la carga sin ahogarla.
    /// </summary>
    private const int ConsultasSimultaneas = 6;

    /// <summary>
    /// Cuánto se reutiliza una consulta de la flota. La consola refresca sola y puede haber varias
    /// personas mirando el mismo mapa: sin esto, cada una multiplicaría las llamadas a MobiControl
    /// (una por equipo) sin ver nada distinto, porque los equipos reportan cada pocos minutos.
    /// </summary>
    private static readonly TimeSpan VigenciaFlota = TimeSpan.FromSeconds(25);

    public async Task<ConfiguracionDto> ConfiguracionAsync(CancellationToken ct = default)
    {
        var config = await configuracion.ObtenerAsync(EmpresaRequerida(), ct);
        return new ConfiguracionDto(config?.MobiControlConfigurado ?? false, config?.TenantNombre);
    }

    public async Task<FlotaDto> ListarAsync(bool refrescar = false, CancellationToken ct = default)
    {
        var empresaId = EmpresaRequerida();
        var clave = $"flota:{empresaId}";

        var instantanea = !refrescar && cache.TryGetValue<Instantanea>(clave, out var enCache) && enCache is not null
            ? enCache
            : await ConsultarMobiControlAsync(clave, ct);

        // Las zonas se cruzan siempre con las vigentes: una geocerca recién dibujada debe verse
        // aplicada sin esperar a que venza la copia de la flota.
        var geocercas = await db.Geocercas.AsNoTracking().Where(g => g.Activa).ToListAsync(ct);

        var equipos = instantanea.Equipos
            .Select(e =>
            {
                instantanea.Ubicaciones.TryGetValue(e.DeviceId, out var u);
                return AVista(e, u, u is null ? [] : Dentro(geocercas, u.Latitud, u.Longitud));
            })
            .OrderByDescending(e => e.EnLinea)
            .ThenBy(e => e.Nombre, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return new FlotaDto(equipos, instantanea.Consultado);
    }

    // ------------------------------------------------------------------------------------

    private sealed record Instantanea(
        IReadOnlyList<EquipoMobiControl> Equipos,
        IReadOnlyDictionary<string, UbicacionMobiControl> Ubicaciones,
        DateTimeOffset Consultado);

    private async Task<Instantanea> ConsultarMobiControlAsync(string clave, CancellationToken ct)
    {
        var equipos = await mobiControl.ListarEquiposAsync(ct);

        // La posición se pide solo a teléfonos y tabletas, en paralelo con tope. Un equipo que no
        // contesta sale sin punto en el mapa; no tumba la consulta de los demás.
        var ubicaciones = new Dictionary<string, UbicacionMobiControl>(StringComparer.OrdinalIgnoreCase);

        using (var cupo = new SemaphoreSlim(ConsultasSimultaneas))
        {
            await Task.WhenAll(equipos.Where(e => e.TieneGps).Select(async equipo =>
            {
                await cupo.WaitAsync(ct);
                try
                {
                    if (await mobiControl.UltimaUbicacionAsync(equipo.DeviceId, ct) is { } ubicacion)
                        lock (ubicaciones) ubicaciones[equipo.DeviceId] = ubicacion;
                }
                catch (ErrorSolicitudException ex)
                {
                    logger.LogDebug("Sin última posición para {Equipo}: {Motivo}", equipo.DeviceId, ex.Message);
                }
                finally
                {
                    cupo.Release();
                }
            }));
        }

        var instantanea = new Instantanea(equipos, ubicaciones, DateTimeOffset.UtcNow);
        cache.Set(clave, instantanea, VigenciaFlota);
        return instantanea;
    }

    private static List<Guid> Dentro(IEnumerable<Geocerca> geocercas, double lat, double lng) =>
        [.. geocercas.Where(g => Geometria.Contiene(g, lat, lng)).Select(g => g.GeocercaUid)];

    private int EmpresaRequerida() =>
        empresa.EmpresaId
        ?? throw new ErrorSolicitudException("Elige una empresa para ver su flota.");

    internal static EquipoFlotaDto AVista(EquipoMobiControl e, UbicacionMobiControl? u, IReadOnlyList<Guid> geocercas) =>
        new(e.DeviceId, e.Nombre, e.Plataforma, e.Fabricante, e.Modelo, e.EnLinea, e.Bateria, e.Cargando,
            e.UltimoReporte, e.Grupo, e.Imei, e.Telefono, e.Serial, e.TieneGps,
            u?.Latitud, u?.Longitud, u?.Momento, u?.Velocidad, u?.Rumbo, geocercas);
}
