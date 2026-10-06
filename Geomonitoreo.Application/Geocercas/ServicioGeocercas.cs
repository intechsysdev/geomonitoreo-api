using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Geomonitoreo.Application.Common;
using Geomonitoreo.Application.Common.Interfaces;
using Geomonitoreo.Domain.Entities;

namespace Geomonitoreo.Application.Geocercas;

/// <param name="EnMobiControl">False si MobiControl ya no la tiene (la borraron en la consola).</param>
/// <param name="ReferenceId">Identificador en MobiControl.</param>
public record GeocercaDto(
    Guid GeocercaUid,
    string Nombre,
    string? Descripcion,
    TipoGeocerca Tipo,
    double Latitud,
    double Longitud,
    double? RadioMetros,
    IReadOnlyList<double[]> Vertices,
    string Color,
    bool Activa,
    bool EnMobiControl,
    string? ReferenceId,
    DateTime? FechaSincronizacion,
    string? CreadaPor,
    DateTime FechaCreacion,
    DateTime? FechaActualizacion);

/// <param name="Sincronizadas">False si MobiControl no respondió: se muestran las formas guardadas.</param>
/// <param name="Aviso">Por qué no se pudo sincronizar.</param>
public record ListaGeocercasDto(IReadOnlyList<GeocercaDto> Geocercas, bool Sincronizadas, string? Aviso);

/// <summary>
/// Una zona. Para un círculo, centro y radio; para un polígono, sus vértices como <c>[lng, lat]</c>
/// (el centro se calcula). En MobiControl el círculo se guarda como un polígono de 48 lados.
/// </summary>
public class GuardarGeocercaRequest
{
    [Required, MaxLength(100)] public string Nombre { get; set; } = string.Empty;
    [MaxLength(500)] public string? Descripcion { get; set; }
    public TipoGeocerca Tipo { get; set; }
    public double? Latitud { get; set; }
    public double? Longitud { get; set; }
    public double? RadioMetros { get; set; }
    public List<double[]>? Vertices { get; set; }
    [MaxLength(7)] public string? Color { get; set; }
    public bool Activa { get; set; } = true;
}

public class ImportarGeocercaRequest
{
    /// <summary>Nombre exacto de la geocerca en MobiControl.</summary>
    [Required, MaxLength(100)] public string Nombre { get; set; } = string.Empty;
}

public interface IServicioGeocercas
{
    Task<ListaGeocercasDto> ListarAsync(CancellationToken ct = default);
    Task<GeocercaDto> CrearAsync(GuardarGeocercaRequest solicitud, CancellationToken ct = default);
    Task<GeocercaDto> ActualizarAsync(Guid uid, GuardarGeocercaRequest solicitud, CancellationToken ct = default);
    Task EliminarAsync(Guid uid, CancellationToken ct = default);

    /// <summary>Trae una geocerca creada en la consola de MobiControl, por su nombre.</summary>
    Task<GeocercaDto> ImportarAsync(string nombre, CancellationToken ct = default);
}

/// <summary>
/// Geocercas de la empresa, guardadas en MobiControl. Geomonitoreo lleva el índice: la API de la
/// consola no las lista, así que se recuerda cuáles hay y se piden por nombre.
/// </summary>
public partial class ServicioGeocercas(
    IApplicationDbContext db,
    IContextoEmpresa empresa,
    IClienteMobiControl mobiControl,
    ILogger<ServicioGeocercas> logger) : IServicioGeocercas
{
    private const int MaximoVertices = 500;

    /// <summary>Lados del polígono con que se manda un círculo: a esta escala no se nota la diferencia.</summary>
    private const int LadosCirculo = 48;

    private const int ConsultasSimultaneas = 4;

    public async Task<ListaGeocercasDto> ListarAsync(CancellationToken ct = default)
    {
        Requerida();
        var geocercas = await db.Geocercas.OrderBy(g => g.Nombre).ToListAsync(ct);

        string? aviso = null;
        try
        {
            await SincronizarAsync(geocercas, ct);
            await db.SaveChangesAsync(ct);
        }
        catch (ErrorSolicitudException ex)
        {
            // Sin consola no se tumba la pantalla: se muestran las formas guardadas y se dice por qué.
            aviso = ex.Message;
        }

        return new ListaGeocercasDto([.. geocercas.Select(AVista)], aviso is null, aviso);
    }

    public async Task<GeocercaDto> CrearAsync(GuardarGeocercaRequest solicitud, CancellationToken ct = default)
    {
        var empresaId = Requerida();
        var geocerca = new Geocerca { EmpresaId = empresaId, CreadaPor = empresa.Usuario, FechaCreacion = DateTime.UtcNow };

        Aplicar(geocerca, solicitud);
        await NombreLibreAsync(geocerca.Nombre, null, ct);

        // Primero en MobiControl: si la consola la rechaza, aquí no queda nada a medias.
        var enConsola = await mobiControl.CrearGeocercaAsync(geocerca.Nombre, VerticesParaMobiControl(geocerca), ct);
        Sincronizada(geocerca, enConsola);

        db.Geocercas.Add(geocerca);
        await db.SaveChangesAsync(ct);
        return AVista(geocerca);
    }

    public async Task<GeocercaDto> ActualizarAsync(Guid uid, GuardarGeocercaRequest solicitud, CancellationToken ct = default)
    {
        Requerida();
        var geocerca = await Buscar(uid, ct);

        var nombreAnterior = geocerca.Nombre;
        var formaAnterior = (geocerca.Tipo, geocerca.Latitud, geocerca.Longitud, geocerca.RadioMetros, geocerca.Vertices);
        var verticesAnteriores = VerticesParaMobiControl(geocerca);

        Aplicar(geocerca, solicitud);
        await NombreLibreAsync(geocerca.Nombre, geocerca.GeocercaId, ct);

        var cambioNombre = nombreAnterior != geocerca.Nombre;
        var cambioForma = formaAnterior != (geocerca.Tipo, geocerca.Latitud, geocerca.Longitud, geocerca.RadioMetros, geocerca.Vertices);

        // Color, descripción y si está activa son solo de Geomonitoreo: no hace falta ir a la consola,
        // salvo que la geocerca ya no esté allá; entonces guardarla es volver a crearla.
        GeocercaMobiControl? enConsola = null;
        if (!geocerca.ExisteEnMobiControl)
            enConsola = await mobiControl.CrearGeocercaAsync(geocerca.Nombre, VerticesParaMobiControl(geocerca), ct);
        else if (cambioForma)
            enConsola = await mobiControl.ReemplazarGeocercaAsync(nombreAnterior, verticesAnteriores, geocerca.Nombre, VerticesParaMobiControl(geocerca), ct);
        else if (cambioNombre)
            enConsola = await mobiControl.RenombrarGeocercaAsync(nombreAnterior, geocerca.Nombre, ct);

        if (enConsola is not null) Sincronizada(geocerca, enConsola);

        geocerca.FechaActualizacion = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return AVista(geocerca);
    }

    public async Task EliminarAsync(Guid uid, CancellationToken ct = default)
    {
        Requerida();
        var geocerca = await Buscar(uid, ct);

        if (geocerca.ExisteEnMobiControl && !await mobiControl.EliminarGeocercaAsync(geocerca.Nombre, ct))
            logger.LogInformation("La geocerca {Nombre} ya no estaba en MobiControl; se quita de la lista.", geocerca.Nombre);

        db.Geocercas.Remove(geocerca);
        await db.SaveChangesAsync(ct);
    }

    public async Task<GeocercaDto> ImportarAsync(string nombre, CancellationToken ct = default)
    {
        var empresaId = Requerida();
        var limpio = nombre?.Trim();
        if (string.IsNullOrEmpty(limpio)) throw new ErrorSolicitudException("Escriba el nombre de la geocerca tal como está en MobiControl.");

        await NombreLibreAsync(limpio, null, ct);

        var enConsola = await mobiControl.ObtenerGeocercaAsync(limpio, ct)
            ?? throw new ErrorSolicitudException(
                $"MobiControl no tiene una geocerca llamada \"{limpio}\". El nombre debe ser exacto, con mayúsculas y espacios.");

        if (enConsola.Vertices.Count < 3)
            throw new ErrorSolicitudException("La geocerca de MobiControl no tiene un contorno que se pueda dibujar.");

        var geocerca = new Geocerca
        {
            EmpresaId = empresaId,
            Nombre = enConsola.Nombre,
            Tipo = TipoGeocerca.POLIGONO,
            Color = "#a855f7",
            CreadaPor = empresa.Usuario,
            FechaCreacion = DateTime.UtcNow,
        };
        Sincronizada(geocerca, enConsola);

        db.Geocercas.Add(geocerca);
        await db.SaveChangesAsync(ct);
        return AVista(geocerca);
    }

    // ------------------------------------------------------------------------------------

    /// <summary>
    /// Trae de MobiControl la forma vigente de cada geocerca, de a varias a la vez. Si alguien la
    /// cambió en la consola, aquí se ve el cambio; si la borró, queda marcada.
    /// </summary>
    private async Task SincronizarAsync(List<Geocerca> geocercas, CancellationToken ct)
    {
        if (geocercas.Count == 0) return;

        var resultados = new Dictionary<int, GeocercaMobiControl?>();
        using var cupo = new SemaphoreSlim(ConsultasSimultaneas);

        await Task.WhenAll(geocercas.Select(async g =>
        {
            await cupo.WaitAsync(ct);
            try
            {
                var enConsola = await mobiControl.ObtenerGeocercaAsync(g.Nombre, ct);
                lock (resultados) resultados[g.GeocercaId] = enConsola;
            }
            finally
            {
                cupo.Release();
            }
        }));

        foreach (var g in geocercas)
        {
            if (resultados[g.GeocercaId] is { } enConsola)
            {
                // Un círculo dibujado aquí vuelve como polígono: se conserva como círculo mientras
                // nadie lo haya cambiado en la consola.
                var esElMismoCirculo = g.Tipo == TipoGeocerca.CIRCULO && enConsola.Vertices.Count == LadosCirculo;
                if (!esElMismoCirculo) g.Tipo = TipoGeocerca.POLIGONO;
                Sincronizada(g, enConsola);
            }
            else
            {
                g.ExisteEnMobiControl = false;
            }
        }
    }

    private static void Sincronizada(Geocerca g, GeocercaMobiControl enConsola)
    {
        g.ExisteEnMobiControl = true;
        g.ReferenceIdMobiControl = enConsola.ReferenceId ?? g.ReferenceIdMobiControl;
        g.FechaSincronizacion = DateTime.UtcNow;

        if (g.Tipo == TipoGeocerca.POLIGONO && enConsola.Vertices.Count >= 3)
        {
            g.Vertices = JsonSerializer.Serialize(enConsola.Vertices);
            g.Latitud = (enConsola.Vertices.Min(v => v[1]) + enConsola.Vertices.Max(v => v[1])) / 2;
            g.Longitud = (enConsola.Vertices.Min(v => v[0]) + enConsola.Vertices.Max(v => v[0])) / 2;
            g.RadioMetros = null;
        }
    }

    /// <summary>Lo que se manda a MobiControl: el círculo, convertido en polígono.</summary>
    private static IReadOnlyList<double[]> VerticesParaMobiControl(Geocerca g) =>
        g.Tipo == TipoGeocerca.CIRCULO
            ? Geometria.Circulo(g.Latitud, g.Longitud, g.RadioMetros ?? 0, LadosCirculo)
            : Geometria.LeerVertices(g.Vertices);

    private async Task NombreLibreAsync(string nombre, int? excepto, CancellationToken ct)
    {
        if (await db.Geocercas.AnyAsync(g => g.Nombre == nombre && g.GeocercaId != excepto, ct))
            throw new ErrorSolicitudException($"Ya hay una geocerca llamada \"{nombre}\".");
    }

    private async Task<Geocerca> Buscar(Guid uid, CancellationToken ct) =>
        await db.Geocercas.FirstOrDefaultAsync(g => g.GeocercaUid == uid, ct)
        ?? throw new ErrorSolicitudException("No existe esa geocerca.");

    /// <summary>
    /// Valida y copia. La geometría se revisa aquí y no solo en el mapa: una zona mal formada no
    /// falla al guardarse sino después, en silencio, cuando ningún equipo aparece dentro.
    /// </summary>
    private static void Aplicar(Geocerca geocerca, GuardarGeocercaRequest s)
    {
        var nombre = s.Nombre?.Trim();
        if (string.IsNullOrEmpty(nombre)) throw new ErrorSolicitudException("La geocerca necesita un nombre.");

        // El nombre viaja en la ruta de MobiControl (/geofences/{nombre}).
        if (nombre.IndexOfAny(['/', '\\', '?', '#', '%']) >= 0)
            throw new ErrorSolicitudException("El nombre no puede llevar / \\ ? # ni %.");

        geocerca.Nombre = nombre;
        geocerca.Descripcion = string.IsNullOrWhiteSpace(s.Descripcion) ? null : s.Descripcion.Trim();
        geocerca.Tipo = s.Tipo;
        geocerca.Activa = s.Activa;
        geocerca.Color = s.Color is { } color && ColorHex().IsMatch(color) ? color.ToLowerInvariant() : "#0ea5e9";

        switch (s.Tipo)
        {
            case TipoGeocerca.CIRCULO:
                if (s.Latitud is not { } lat || s.Longitud is not { } lng || !Geometria.CoordenadaValida(lat, lng))
                    throw new ErrorSolicitudException("El círculo necesita un centro válido.");

                if (s.RadioMetros is not { } radio || radio < 10 || radio > 100_000)
                    throw new ErrorSolicitudException("El radio debe estar entre 10 m y 100 km.");

                geocerca.Latitud = lat;
                geocerca.Longitud = lng;
                geocerca.RadioMetros = Math.Round(radio, 1);
                geocerca.Vertices = JsonSerializer.Serialize(Geometria.Circulo(lat, lng, radio, LadosCirculo));
                break;

            case TipoGeocerca.POLIGONO:
                var vertices = (s.Vertices ?? []).Where(v => v is { Length: >= 2 }).Select(v => new[] { v[0], v[1] }).ToList();

                // Si el front cerró el anillo repitiendo el primer vértice al final, se quita: aquí
                // se guarda abierto, y repetido contaría doble en el cálculo.
                if (vertices.Count > 1 && vertices[0][0] == vertices[^1][0] && vertices[0][1] == vertices[^1][1])
                    vertices.RemoveAt(vertices.Count - 1);

                if (vertices.Count < 3)
                    throw new ErrorSolicitudException("El polígono necesita al menos tres vértices.");

                if (vertices.Count > MaximoVertices)
                    throw new ErrorSolicitudException($"El polígono puede tener hasta {MaximoVertices} vértices.");

                if (vertices.Any(v => !Geometria.CoordenadaValida(v[1], v[0])))
                    throw new ErrorSolicitudException("Hay vértices con coordenadas inválidas.");

                geocerca.Vertices = JsonSerializer.Serialize(vertices);
                geocerca.Latitud = (vertices.Min(v => v[1]) + vertices.Max(v => v[1])) / 2;
                geocerca.Longitud = (vertices.Min(v => v[0]) + vertices.Max(v => v[0])) / 2;
                geocerca.RadioMetros = null;
                break;

            default:
                throw new ErrorSolicitudException("Tipo de geocerca desconocido.");
        }
    }

    private int Requerida() =>
        empresa.EmpresaId ?? throw new ErrorSolicitudException("Elige una empresa para administrar sus geocercas.");

    private static GeocercaDto AVista(Geocerca g) => new(
        g.GeocercaUid, g.Nombre, g.Descripcion, g.Tipo, g.Latitud, g.Longitud, g.RadioMetros,
        Geometria.LeerVertices(g.Vertices), g.Color, g.Activa, g.ExisteEnMobiControl, g.ReferenceIdMobiControl,
        Utc(g.FechaSincronizacion), g.CreadaPor, Utc(g.FechaCreacion)!.Value, Utc(g.FechaActualizacion));

    private static DateTime? Utc(DateTime? fecha) => fecha is { } f ? DateTime.SpecifyKind(f, DateTimeKind.Utc) : null;

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex ColorHex();
}
