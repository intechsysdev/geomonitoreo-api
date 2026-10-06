namespace Geomonitoreo.Domain.Entities;

public enum TipoGeocerca
{
    /// <summary>Centro y radio: una sede, un punto de venta.</summary>
    CIRCULO,

    /// <summary>Contorno dibujado: una zona, un barrio, un distrito.</summary>
    POLIGONO,
}

/// <summary>
/// Zona del mapa contra la que se compara la posición de los equipos: qué equipos están dentro,
/// cuáles salieron. Es de la empresa, no de MobiControl: la consola no sabe de zonas.
/// </summary>
public class Geocerca : IDeEmpresa
{
    public int GeocercaId { get; set; }

    public int EmpresaId { get; set; }
    public Empresa Empresa { get; set; } = null!;

    /// <summary>Identificador público, el que viaja al front.</summary>
    public Guid GeocercaUid { get; set; } = Guid.NewGuid();

    public string Nombre { get; set; } = string.Empty;
    public string? Descripcion { get; set; }

    public TipoGeocerca Tipo { get; set; }

    /// <summary>Centro del círculo. En un polígono, el centro de su caja: sirve para encuadrar el mapa.</summary>
    public double Latitud { get; set; }
    public double Longitud { get; set; }

    /// <summary>Solo para círculos.</summary>
    public double? RadioMetros { get; set; }

    /// <summary>
    /// Solo para polígonos: los vértices como JSON <c>[[lng, lat], …]</c>, el mismo orden de GeoJSON
    /// que usa el mapa. Sin repetir el primero al final.
    /// </summary>
    public string? Vertices { get; set; }

    /// <summary>Color en el mapa, como <c>#rrggbb</c>.</summary>
    public string Color { get; set; } = "#0ea5e9";

    public bool Activa { get; set; } = true;

    public string? CreadaPor { get; set; }
    public DateTime FechaCreacion { get; set; }
    public DateTime? FechaActualizacion { get; set; }
}
