using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Geomonitoreo.Application.Common;
using Geomonitoreo.Application.Common.Interfaces;
using Geomonitoreo.Domain.Entities;

namespace Geomonitoreo.Application.Geocercas;

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
    string? CreadaPor,
    DateTime FechaCreacion,
    DateTime? FechaActualizacion);

/// <summary>
/// Una zona. Para un círculo, centro y radio; para un polígono, sus vértices como <c>[lng, lat]</c>
/// (el centro se calcula).
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

public interface IServicioGeocercas
{
    Task<IReadOnlyList<GeocercaDto>> ListarAsync(CancellationToken ct = default);
    Task<GeocercaDto> CrearAsync(GuardarGeocercaRequest solicitud, CancellationToken ct = default);
    Task<GeocercaDto> ActualizarAsync(Guid uid, GuardarGeocercaRequest solicitud, CancellationToken ct = default);
    Task EliminarAsync(Guid uid, CancellationToken ct = default);
}

public partial class ServicioGeocercas(IApplicationDbContext db, IContextoEmpresa empresa) : IServicioGeocercas
{
    private const int MaximoVertices = 500;

    public async Task<IReadOnlyList<GeocercaDto>> ListarAsync(CancellationToken ct = default)
    {
        Requerida();
        var geocercas = await db.Geocercas.AsNoTracking().OrderBy(g => g.Nombre).ToListAsync(ct);
        return [.. geocercas.Select(AVista)];
    }

    public async Task<GeocercaDto> CrearAsync(GuardarGeocercaRequest solicitud, CancellationToken ct = default)
    {
        var geocerca = new Geocerca
        {
            EmpresaId = Requerida(),
            CreadaPor = empresa.Usuario,
            FechaCreacion = DateTime.UtcNow,
        };

        Aplicar(geocerca, solicitud);

        db.Geocercas.Add(geocerca);
        await db.SaveChangesAsync(ct);
        return AVista(geocerca);
    }

    public async Task<GeocercaDto> ActualizarAsync(Guid uid, GuardarGeocercaRequest solicitud, CancellationToken ct = default)
    {
        Requerida();
        var geocerca = await Buscar(uid, ct);

        Aplicar(geocerca, solicitud);
        geocerca.FechaActualizacion = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
        return AVista(geocerca);
    }

    public async Task EliminarAsync(Guid uid, CancellationToken ct = default)
    {
        Requerida();
        db.Geocercas.Remove(await Buscar(uid, ct));
        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------------------------

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
                geocerca.Vertices = null;
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
        Geometria.LeerVertices(g.Vertices), g.Color, g.Activa, g.CreadaPor,
        DateTime.SpecifyKind(g.FechaCreacion, DateTimeKind.Utc),
        g.FechaActualizacion is { } f ? DateTime.SpecifyKind(f, DateTimeKind.Utc) : null);

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex ColorHex();
}
