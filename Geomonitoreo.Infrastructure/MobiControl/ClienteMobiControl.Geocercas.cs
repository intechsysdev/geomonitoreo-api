using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Geomonitoreo.Application.Common;
using Geomonitoreo.Application.Common.Interfaces;

namespace Geomonitoreo.Infrastructure.MobiControl;

/// <summary>
/// Geocercas en MobiControl. Lo que se sabe de su API (no está en la documentación pública; se
/// comprobó contra la consola):
///
/// - <c>POST /geofences</c> crea: <c>{ Name, Vertices: [{ Latitude, Longitude }] }</c>. El polígono
///   tiene que venir cerrado (el primer vértice repetido al final) y con al menos cuatro puntos.
/// - <c>GET /geofences/{nombre}</c> la lee; si no existe responde 403, no 404.
/// - <c>PUT /geofences/{nombre}</c> solo la renombra: ignora los vértices, y rechaza el cuerpo si el
///   nombre es el mismo que ya tiene ("ya existe"). Para cambiar la forma hay que borrarla y crearla.
/// - <c>DELETE /geofences/{nombre}</c> la borra.
/// - <c>GET /geofences/summary</c> las lista todas, solo con <c>Name</c> y <c>ReferenceId</c>.
/// </summary>
public partial class ClienteMobiControl
{
    public async Task<IReadOnlyList<ResumenGeocercaMobiControl>> ListarGeocercasAsync(CancellationToken ct = default)
    {
        var (config, empresaId, token) = await SesionAsync(ct);
        var (codigo, texto) = await GeocercaAsync(empresaId, config, token, HttpMethod.Get, "summary", null, ct);
        Asegurar(codigo, texto);

        using var json = JsonDocument.Parse(texto);
        if (json.RootElement.ValueKind != JsonValueKind.Array) return [];

        return
        [
            .. json.RootElement.EnumerateArray()
                .Select(g => new ResumenGeocercaMobiControl(Texto(g, "Name") ?? string.Empty, Texto(g, "ReferenceId")))
                .Where(g => g.Nombre.Length > 0)
        ];
    }

    public async Task<GeocercaMobiControl?> ObtenerGeocercaAsync(string nombre, CancellationToken ct = default)
    {
        var (config, empresaId, token) = await SesionAsync(ct);
        var (codigo, texto) = await GeocercaAsync(empresaId, config, token, HttpMethod.Get, nombre, null, ct);

        if (codigo is 403 or 404) return null;
        Asegurar(codigo, texto);
        return LeerGeocerca(texto);
    }

    public async Task<GeocercaMobiControl> CrearGeocercaAsync(
        string nombre, IReadOnlyList<double[]> vertices, CancellationToken ct = default)
    {
        var (config, empresaId, token) = await SesionAsync(ct);
        var (codigo, texto) = await GeocercaAsync(empresaId, config, token, HttpMethod.Post, null, Cuerpo(nombre, vertices), ct);

        Asegurar(codigo, texto);
        return LeerGeocerca(texto);
    }

    public async Task<GeocercaMobiControl> RenombrarGeocercaAsync(
        string nombreActual, string nombreNuevo, CancellationToken ct = default)
    {
        var (config, empresaId, token) = await SesionAsync(ct);

        if (!string.Equals(nombreActual, nombreNuevo, StringComparison.Ordinal))
        {
            // PUT exige vértices en el cuerpo aunque no los use: se mandan los que ya tiene.
            var actual = await ObtenerGeocercaAsync(nombreActual, ct);
            NoExiste(actual is null ? 403 : 200, nombreActual);

            var (codigo, texto) = await GeocercaAsync(empresaId, config, token, HttpMethod.Put, nombreActual, Cuerpo(nombreNuevo, actual!.Vertices), ct);
            NoExiste(codigo, nombreActual);
            Asegurar(codigo, texto);
        }

        var (final, cuerpo) = await GeocercaAsync(empresaId, config, token, HttpMethod.Get, nombreNuevo, null, ct);
        Asegurar(final, cuerpo);
        return LeerGeocerca(cuerpo);
    }

    public async Task<GeocercaMobiControl> ReemplazarGeocercaAsync(
        string nombreActual, IReadOnlyList<double[]> verticesActuales, string nombreNuevo, IReadOnlyList<double[]> vertices,
        CancellationToken ct = default)
    {
        var (config, empresaId, token) = await SesionAsync(ct);

        // Si el nombre nuevo es de otra geocerca, se rechaza antes de borrar nada.
        if (!string.Equals(nombreActual, nombreNuevo, StringComparison.Ordinal) && await ObtenerGeocercaAsync(nombreNuevo, ct) is not null)
            Asegurar(422, "{\"ErrorCode\":3200}");

        var (borrado, textoBorrado) = await GeocercaAsync(empresaId, config, token, HttpMethod.Delete, nombreActual, null, ct);
        if (borrado is not (403 or 404)) Asegurar(borrado, textoBorrado);

        var (codigo, texto) = await GeocercaAsync(empresaId, config, token, HttpMethod.Post, null, Cuerpo(nombreNuevo, vertices), ct);
        if (codigo is >= 200 and < 300) return LeerGeocerca(texto);

        // No se pudo crear la nueva: se deja la anterior como estaba, para no perder la geocerca.
        if (borrado is >= 200 and < 300 && verticesActuales.Count >= 3)
        {
            var (repuesta, detalle) = await GeocercaAsync(empresaId, config, token, HttpMethod.Post, null, Cuerpo(nombreActual, verticesActuales), ct);
            if (repuesta is < 200 or >= 300)
                logger.LogError("No se pudo reponer la geocerca {Nombre} en MobiControl: {Detalle}", nombreActual, detalle);
        }

        Asegurar(codigo, texto);
        throw new InvalidOperationException("No debería llegar aquí.");
    }

    public async Task<bool> EliminarGeocercaAsync(string nombre, CancellationToken ct = default)
    {
        var (config, empresaId, token) = await SesionAsync(ct);
        var (codigo, texto) = await GeocercaAsync(empresaId, config, token, HttpMethod.Delete, nombre, null, ct);

        if (codigo is 403 or 404) return false;
        Asegurar(codigo, texto);
        return true;
    }

    // ------------------------------------------------------------------------------------

    private async Task<(int Codigo, string Texto)> GeocercaAsync(
        int empresaId, ConfiguracionEmpresa config, string token, HttpMethod metodo, string? nombre, object? cuerpo, CancellationToken ct)
    {
        var ruta = nombre is null ? "api/geofences" : $"api/geofences/{Uri.EscapeDataString(nombre)}";

        using var limite = ConLimite(config, ct);
        using var peticion = new HttpRequestMessage(metodo, Ruta(config, ruta));
        peticion.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        peticion.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (cuerpo is not null)
            peticion.Content = new StringContent(JsonSerializer.Serialize(cuerpo), Encoding.UTF8, "application/json");

        try
        {
            using var respuesta = await http.SendAsync(peticion, limite.Token);
            var codigo = (int)respuesta.StatusCode;
            if (codigo == 401) Tokens.TryRemove(empresaId, out _);
            return (codigo, await respuesta.Content.ReadAsStringAsync(ct));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ErrorSolicitudException("MobiControl no respondió a tiempo. Intente de nuevo en un momento.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "No se pudo conectar con MobiControl de {Empresa}.", config.TenantNombre);
            throw new ErrorSolicitudException("No se pudo conectar con la consola de MobiControl.");
        }
    }

    /// <summary>Lo que MobiControl espera: vértices como objetos y el polígono cerrado.</summary>
    private static object Cuerpo(string nombre, IReadOnlyList<double[]> vertices)
    {
        var puntos = vertices.Select(v => new { Latitude = v[1], Longitude = v[0] }).ToList();
        if (puntos.Count > 0 && (puntos[0].Latitude != puntos[^1].Latitude || puntos[0].Longitude != puntos[^1].Longitude))
            puntos.Add(puntos[0]);

        return new { Name = nombre, Vertices = puntos };
    }

    private static GeocercaMobiControl LeerGeocerca(string texto)
    {
        using var json = JsonDocument.Parse(texto);
        var raiz = json.RootElement;

        var vertices = new List<double[]>();
        if (raiz.TryGetProperty("Vertices", out var lista) && lista.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in lista.EnumerateArray())
            {
                if (Decimal(v, "Latitude") is { } lat && Decimal(v, "Longitude") is { } lng)
                    vertices.Add([lng, lat]);
            }
        }

        // Aquí se guardan abiertas: el cierre lo pone quien dibuja.
        if (vertices.Count > 1 && vertices[0][0] == vertices[^1][0] && vertices[0][1] == vertices[^1][1])
            vertices.RemoveAt(vertices.Count - 1);

        return new GeocercaMobiControl(Texto(raiz, "Name") ?? string.Empty, Texto(raiz, "ReferenceId"), vertices);
    }

    private static void NoExiste(int codigo, string nombre)
    {
        if (codigo is 403 or 404)
            throw new ErrorSolicitudException($"La geocerca \"{nombre}\" ya no está en MobiControl. Quítela de la lista o vuelva a crearla.");
    }

    /// <summary>Traduce los rechazos de MobiControl a algo que se pueda mostrar.</summary>
    private static void Asegurar(int codigo, string texto)
    {
        if (codigo is >= 200 and < 300) return;

        int? error = null;
        string? mensaje = null;
        try
        {
            using var json = JsonDocument.Parse(texto);
            if (json.RootElement.TryGetProperty("ErrorCode", out var e) && e.TryGetInt32(out var n)) error = n;
            mensaje = Texto(json.RootElement, "Message");
        }
        catch (JsonException)
        {
            // Sin cuerpo legible: queda el código.
        }

        throw new ErrorSolicitudException(error switch
        {
            3200 => "Ya existe en MobiControl una geocerca con ese nombre. Use otro nombre, o impórtela si es la misma.",
            3201 => "MobiControl necesita al menos tres puntos distintos para una geocerca.",
            3204 => "MobiControl recibió un polígono sin cerrar.",
            _ => $"MobiControl rechazó la geocerca ({codigo}){(string.IsNullOrWhiteSpace(mensaje) ? "." : $": {mensaje}")}",
        });
    }
}
