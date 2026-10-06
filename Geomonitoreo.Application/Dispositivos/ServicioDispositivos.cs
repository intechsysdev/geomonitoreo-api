using Microsoft.EntityFrameworkCore;
using Geomonitoreo.Application.Common;
using Geomonitoreo.Application.Common.Interfaces;
using Geomonitoreo.Domain.Entities;

namespace Geomonitoreo.Application.Dispositivos;

public record DetalleDispositivoDto(
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
    string? VersionSistema,
    string? VersionAgente,
    DateTimeOffset? FechaInscripcion,
    string? Red,
    bool TieneGps,
    double? Latitud,
    double? Longitud,
    DateTimeOffset? FechaUbicacion,
    double? Velocidad,
    double? Rumbo,
    IReadOnlyList<Guid> Geocercas);

/// <param name="VelocidadKmh">Calculada con el tramo anterior: la que reporta el equipo no siempre viene.</param>
public record PuntoRecorridoDto(double Latitud, double Longitud, DateTimeOffset Momento, double? VelocidadKmh, double? Rumbo);

public record ParadaDto(double Latitud, double Longitud, DateTimeOffset Inicio, DateTimeOffset Fin, double Minutos);

public record EventoGeocercaDto(Guid Geocerca, string Nombre, string Tipo, DateTimeOffset Momento, double Latitud, double Longitud);

/// <param name="DistanciaKm">Suma de los tramos, sin los saltos que el GPS no pudo haber recorrido.</param>
/// <param name="MinutosEnMovimiento">Tiempo entre puntos que no forma parte de una parada.</param>
public record EstadisticasRecorridoDto(
    int Puntos,
    double DistanciaKm,
    double DuracionMinutos,
    double MinutosEnMovimiento,
    double VelocidadPromedioKmh,
    double VelocidadMaximaKmh,
    int Paradas);

public record RecorridoDto(
    string DeviceId,
    DateTimeOffset Desde,
    DateTimeOffset Hasta,
    IReadOnlyList<PuntoRecorridoDto> Puntos,
    EstadisticasRecorridoDto Estadisticas,
    IReadOnlyList<ParadaDto> Paradas,
    IReadOnlyList<EventoGeocercaDto> Eventos);

public interface IServicioDispositivos
{
    Task<DetalleDispositivoDto> ObtenerAsync(string deviceId, CancellationToken ct = default);
    Task<RecorridoDto> RecorridoAsync(string deviceId, DateTimeOffset desde, DateTimeOffset hasta, CancellationToken ct = default);
    Task LocalizarAsync(string deviceId, CancellationToken ct = default);
}

/// <summary>
/// Un equipo en detalle y su recorrido. El recorrido no se guarda aquí: lo tiene MobiControl, que
/// recolecta la posición de los equipos según la regla de la consola. Aquí se analiza: distancia,
/// velocidad, dónde se detuvo y por qué zonas pasó.
/// </summary>
public class ServicioDispositivos(
    IApplicationDbContext db,
    IContextoEmpresa empresa,
    IClienteMobiControl mobiControl) : IServicioDispositivos
{
    /// <summary>Más de una semana de puntos es lento de traer y no se lee en un mapa.</summary>
    private static readonly TimeSpan RangoMaximo = TimeSpan.FromDays(7);

    /// <summary>Quedarse en este radio...</summary>
    private const double RadioParadaMetros = 120;

    /// <summary>...al menos este tiempo, es una parada y no un semáforo.</summary>
    private static readonly TimeSpan DuracionMinimaParada = TimeSpan.FromMinutes(5);

    /// <summary>Un tramo más rápido que esto es el GPS saltando, no el equipo moviéndose.</summary>
    private const double VelocidadImposibleKmh = 250;

    /// <summary>Por debajo de esto es caminar en el sitio o el error del GPS, no un desplazamiento.</summary>
    private const double VelocidadMinimaMovimientoKmh = 3;

    /// <summary>Entre dos puntos más separados que esto no se sabe qué pasó: no se cuenta como viaje.</summary>
    private static readonly TimeSpan HuecoMaximo = TimeSpan.FromMinutes(20);

    public async Task<DetalleDispositivoDto> ObtenerAsync(string deviceId, CancellationToken ct = default)
    {
        EmpresaRequerida();

        var equipo = await mobiControl.ObtenerEquipoAsync(deviceId, ct)
            ?? throw new ErrorSolicitudException("MobiControl no tiene un equipo con ese identificador.");

        var ubicacion = equipo.TieneGps ? await mobiControl.UltimaUbicacionAsync(deviceId, ct) : null;

        IReadOnlyList<Guid> geocercas = [];
        if (ubicacion is not null)
        {
            var activas = await db.Geocercas.AsNoTracking().Where(g => g.Activa).ToListAsync(ct);
            geocercas = [.. activas.Where(g => Geometria.Contiene(g, ubicacion.Latitud, ubicacion.Longitud)).Select(g => g.GeocercaUid)];
        }

        return new DetalleDispositivoDto(
            equipo.DeviceId, equipo.Nombre, equipo.Plataforma, equipo.Fabricante, equipo.Modelo, equipo.EnLinea,
            equipo.Bateria, equipo.Cargando, equipo.UltimoReporte, equipo.Grupo, equipo.Imei, equipo.Telefono,
            equipo.Serial, equipo.VersionSistema, equipo.VersionAgente, equipo.FechaInscripcion, equipo.Red,
            equipo.TieneGps, ubicacion?.Latitud, ubicacion?.Longitud, ubicacion?.Momento, ubicacion?.Velocidad,
            ubicacion?.Rumbo, geocercas);
    }

    public async Task<RecorridoDto> RecorridoAsync(
        string deviceId, DateTimeOffset desde, DateTimeOffset hasta, CancellationToken ct = default)
    {
        EmpresaRequerida();

        if (hasta <= desde)
            throw new ErrorSolicitudException("El final del rango debe ser posterior al inicio.");

        if (hasta - desde > RangoMaximo)
            throw new ErrorSolicitudException("El rango del recorrido puede ser de hasta 7 días.");

        var crudos = await mobiControl.RecorridoAsync(deviceId, desde, hasta, ct);
        var geocercas = await db.Geocercas.AsNoTracking().Where(g => g.Activa).ToListAsync(ct);

        var puntos = Depurar(crudos);
        var paradas = Paradas(puntos);
        var eventos = Eventos(puntos, geocercas);

        return new RecorridoDto(deviceId, desde, hasta, puntos, Estadisticas(puntos, paradas), paradas, eventos);
    }

    public async Task LocalizarAsync(string deviceId, CancellationToken ct = default)
    {
        EmpresaRequerida();
        await mobiControl.LocalizarAsync(deviceId, ct);
    }

    // ---- Análisis del recorrido ------------------------------------------------------------

    /// <summary>
    /// Ordena, quita repetidos y los saltos imposibles, y calcula la velocidad de cada tramo. Un
    /// salto de 30 km en un minuto es el GPS que perdió la señal: dibujado, cruza el mapa con una
    /// línea que nunca se recorrió y dispara la distancia total.
    /// </summary>
    private static List<PuntoRecorridoDto> Depurar(IReadOnlyList<UbicacionMobiControl> crudos)
    {
        var salida = new List<PuntoRecorridoDto>(crudos.Count);
        UbicacionMobiControl? anterior = null;

        foreach (var punto in crudos.OrderBy(p => p.Momento))
        {
            if (!Geometria.CoordenadaValida(punto.Latitud, punto.Longitud)) continue;

            double? velocidad = null;

            if (anterior is not null)
            {
                var segundos = (punto.Momento - anterior.Momento).TotalSeconds;
                if (segundos <= 0) continue;

                var metros = Geometria.Distancia(anterior.Latitud, anterior.Longitud, punto.Latitud, punto.Longitud);
                velocidad = metros / segundos * 3.6;

                if (velocidad > VelocidadImposibleKmh) continue;
            }

            salida.Add(new PuntoRecorridoDto(punto.Latitud, punto.Longitud, punto.Momento,
                velocidad is null ? null : Math.Round(velocidad.Value, 1), punto.Rumbo));
            anterior = punto;
        }

        return salida;
    }

    /// <summary>
    /// Agrupa los puntos consecutivos que no se alejan del primero del grupo. Si el grupo dura lo
    /// suficiente, es una parada; se ubica en el centro de sus puntos.
    /// </summary>
    private static List<ParadaDto> Paradas(IReadOnlyList<PuntoRecorridoDto> puntos)
    {
        var paradas = new List<ParadaDto>();
        var inicio = 0;

        for (var i = 1; i <= puntos.Count; i++)
        {
            var sigueCerca = i < puntos.Count &&
                Geometria.Distancia(puntos[inicio].Latitud, puntos[inicio].Longitud, puntos[i].Latitud, puntos[i].Longitud)
                <= RadioParadaMetros;

            if (sigueCerca) continue;

            var grupo = puntos.Skip(inicio).Take(i - inicio).ToList();
            var duracion = grupo[^1].Momento - grupo[0].Momento;

            if (grupo.Count >= 2 && duracion >= DuracionMinimaParada)
                paradas.Add(new ParadaDto(
                    grupo.Average(p => p.Latitud), grupo.Average(p => p.Longitud),
                    grupo[0].Momento, grupo[^1].Momento, Math.Round(duracion.TotalMinutes, 1)));

            inicio = i;
        }

        return paradas;
    }

    private static EstadisticasRecorridoDto Estadisticas(IReadOnlyList<PuntoRecorridoDto> puntos, IReadOnlyList<ParadaDto> paradas)
    {
        if (puntos.Count < 2)
            return new EstadisticasRecorridoDto(puntos.Count, 0, 0, 0, 0, 0, paradas.Count);

        var metros = 0d;
        for (var i = 1; i < puntos.Count; i++)
            metros += Geometria.Distancia(puntos[i - 1].Latitud, puntos[i - 1].Longitud, puntos[i].Latitud, puntos[i].Longitud);

        var duracion = (puntos[^1].Momento - puntos[0].Momento).TotalMinutes;

        // En movimiento: los tramos en que de verdad avanzó. Restar las paradas de la duración no
        // sirve: los huecos sin puntos (el equipo apagado de noche) quedaban contados como viaje.
        var enMovimiento = 0d;
        for (var i = 1; i < puntos.Count; i++)
        {
            var tramo = puntos[i].Momento - puntos[i - 1].Momento;
            if (tramo <= HuecoMaximo && puntos[i].VelocidadKmh >= VelocidadMinimaMovimientoKmh)
                enMovimiento += tramo.TotalMinutes;
        }

        // La máxima, de los tramos con al menos 20 s: entre dos lecturas casi simultáneas, un
        // error de pocos metros parece una velocidad enorme.
        var maxima = 0d;
        for (var i = 1; i < puntos.Count; i++)
        {
            if ((puntos[i].Momento - puntos[i - 1].Momento).TotalSeconds >= 20 && puntos[i].VelocidadKmh is { } v)
                maxima = Math.Max(maxima, v);
        }

        var km = metros / 1000;
        var promedio = enMovimiento > 0 ? km / (enMovimiento / 60) : 0;

        return new EstadisticasRecorridoDto(
            puntos.Count, Math.Round(km, 2), Math.Round(duracion, 1), Math.Round(enMovimiento, 1),
            Math.Round(promedio, 1), Math.Round(maxima, 1), paradas.Count);
    }

    /// <summary>
    /// Entradas y salidas de zonas a lo largo del recorrido. El estado inicial no es un evento: si el
    /// equipo amaneció dentro de la sede, eso no es "entrar".
    /// </summary>
    private static List<EventoGeocercaDto> Eventos(IReadOnlyList<PuntoRecorridoDto> puntos, IReadOnlyList<Geocerca> geocercas)
    {
        var eventos = new List<EventoGeocercaDto>();
        if (geocercas.Count == 0 || puntos.Count == 0) return eventos;

        var dentro = geocercas.ToDictionary(g => g.GeocercaUid, g => Geometria.Contiene(g, puntos[0].Latitud, puntos[0].Longitud));

        foreach (var punto in puntos.Skip(1))
        {
            foreach (var geocerca in geocercas)
            {
                var ahora = Geometria.Contiene(geocerca, punto.Latitud, punto.Longitud);
                if (ahora == dentro[geocerca.GeocercaUid]) continue;

                dentro[geocerca.GeocercaUid] = ahora;
                eventos.Add(new EventoGeocercaDto(geocerca.GeocercaUid, geocerca.Nombre, ahora ? "ENTRADA" : "SALIDA",
                    punto.Momento, punto.Latitud, punto.Longitud));
            }
        }

        return eventos;
    }

    private int EmpresaRequerida() =>
        empresa.EmpresaId ?? throw new ErrorSolicitudException("Elige una empresa para ver sus equipos.");
}
