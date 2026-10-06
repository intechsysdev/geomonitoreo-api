using System.Text.Json;
using Geomonitoreo.Domain.Entities;

namespace Geomonitoreo.Application.Common;

/// <summary>
/// Cálculos sobre la esfera: distancias y pertenencia a una zona. A la escala de una ciudad la
/// aproximación esférica se equivoca en milímetros, y no hace falta una librería de GIS.
/// </summary>
public static class Geometria
{
    private const double RadioTierraMetros = 6_371_008.8;

    /// <summary>Distancia en metros entre dos puntos (fórmula del haversine).</summary>
    public static double Distancia(double lat1, double lng1, double lat2, double lng2)
    {
        var dLat = Radianes(lat2 - lat1);
        var dLng = Radianes(lng2 - lng1);

        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(Radianes(lat1)) * Math.Cos(Radianes(lat2)) * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);

        return 2 * RadioTierraMetros * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    /// <summary>
    /// Si el punto está dentro del polígono (vértices como [lng, lat]). Rayo hacia el este,
    /// contando cruces: en un área urbana la curvatura no cambia el resultado.
    /// </summary>
    public static bool DentroDePoligono(double lat, double lng, IReadOnlyList<double[]> vertices)
    {
        var dentro = false;

        for (int i = 0, j = vertices.Count - 1; i < vertices.Count; j = i++)
        {
            double xi = vertices[i][0], yi = vertices[i][1];
            double xj = vertices[j][0], yj = vertices[j][1];

            if ((yi > lat) != (yj > lat) && lng < (xj - xi) * (lat - yi) / (yj - yi) + xi)
                dentro = !dentro;
        }

        return dentro;
    }

    public static bool Contiene(Geocerca geocerca, double lat, double lng) =>
        geocerca.Tipo switch
        {
            TipoGeocerca.CIRCULO => geocerca.RadioMetros is { } radio &&
                                    Distancia(geocerca.Latitud, geocerca.Longitud, lat, lng) <= radio,
            TipoGeocerca.POLIGONO => LeerVertices(geocerca.Vertices) is { Count: >= 3 } vertices &&
                                     DentroDePoligono(lat, lng, vertices),
            _ => false,
        };

    public static IReadOnlyList<double[]> LeerVertices(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            return JsonSerializer.Deserialize<List<double[]>>(json)?.Where(v => v.Length >= 2).ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Círculo como polígono abierto de <paramref name="lados"/> vértices <c>[lng, lat]</c>.</summary>
    public static IReadOnlyList<double[]> Circulo(double lat, double lng, double radioMetros, int lados)
    {
        var dLat = radioMetros / RadioTierraMetros * (180 / Math.PI);
        var dLng = dLat / Math.Cos(Radianes(lat));

        return
        [
            .. Enumerable.Range(0, lados).Select(i =>
            {
                var t = 2 * Math.PI * i / lados;
                return new[] { Math.Round(lng + dLng * Math.Cos(t), 7), Math.Round(lat + dLat * Math.Sin(t), 7) };
            }),
        ];
    }

    public static bool CoordenadaValida(double lat, double lng) =>
        lat is >= -90 and <= 90 && lng is >= -180 and <= 180 && !(Math.Abs(lat) < 1e-6 && Math.Abs(lng) < 1e-6);

    private static double Radianes(double grados) => grados * Math.PI / 180;
}
