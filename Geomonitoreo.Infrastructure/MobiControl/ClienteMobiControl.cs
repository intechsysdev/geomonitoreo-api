using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using Geomonitoreo.Application.Common.Interfaces;

namespace Geomonitoreo.Infrastructure.MobiControl;

/// <summary>
/// Cliente de la API de MobiControl de cada empresa: el token (en caché por empresa) y las
/// llamadas comunes. Las lecturas de la flota están en el otro archivo de la clase.
/// </summary>
public partial class ClienteMobiControl(
    HttpClient http,
    IProveedorConfiguracion configuracion,
    IContextoEmpresa contextoEmpresa,
    ILogger<ClienteMobiControl> logger) : IClienteMobiControl
{
    // El token dura ~una hora y se reutiliza entre consultas: pedir uno por llamada duplicaría
    // las peticiones a la consola. La caché es por empresa porque cada una tiene su propia
    // consola: un token de una no sirve —ni debe servir— en otra.
    private static readonly ConcurrentDictionary<int, TokenEnCache> Tokens = new();
    private static readonly SemaphoreSlim Candado = new(1, 1);

    private sealed record TokenEnCache(string Token, DateTime Expira);

    private const string AccionToken = "OBTENER_TOKEN";

    /// <summary>Resultado de una llamada a MobiControl.</summary>
    private sealed record ResultadoIntegracion(string Accion, bool Exitoso, int? CodigoHttp, string? MensajeError);

    private async Task<ConfiguracionEmpresa?> ConfigAsync(CancellationToken ct)
    {
        if (contextoEmpresa.EmpresaId is not { } id) return null;
        return await configuracion.ObtenerAsync(id, ct);
    }

    public async Task<bool> EstaConfiguradoAsync(CancellationToken ct = default) =>
        await ConfigAsync(ct) is { MobiControlConfigurado: true };

    /// <summary>
    /// La barra final es obligatoria al componer: sin ella, Uri descarta el último segmento de
    /// la ruta y las peticiones salen a /api/token en la raíz del host en vez de bajo
    /// /mobicontrol. Antes lo resolvía BaseAddress, que ya no sirve porque cada empresa tiene
    /// una consola distinta y el HttpClient es compartido.
    private static Uri Ruta(ConfiguracionEmpresa config, string relativa) =>
        new(new Uri(config.MobiControlBaseUrl!.TrimEnd('/') + "/"), relativa);

    /// <summary>Plazo de cada llamada: una consola que no responde no puede dejar colgado el mapa.</summary>
    private static CancellationTokenSource ConLimite(ConfiguracionEmpresa config, CancellationToken ct)
    {
        var fuente = CancellationTokenSource.CreateLinkedTokenSource(ct);
        fuente.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, config.MobiControlTimeoutSegundos)));
        return fuente;
    }

    private async Task<(string? Token, ResultadoIntegracion Resultado)> ObtenerTokenAsync(
        int empresaId, ConfiguracionEmpresa config, CancellationToken ct)
    {
        await Candado.WaitAsync(ct);
        try
        {
            if (Tokens.TryGetValue(empresaId, out var enCache) && DateTime.UtcNow < enCache.Expira)
                return (enCache.Token, new ResultadoIntegracion(AccionToken, true, null, "Token en caché."));

            using var limite = ConLimite(config, ct);
            using var peticion = new HttpRequestMessage(HttpMethod.Post, Ruta(config, "api/token"))
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "password",
                    ["username"] = config.MobiControlUsuario!,
                    ["password"] = config.MobiControlPassword!,
                }),
            };

            var credencial = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{config.MobiControlClientId}:{config.MobiControlClientSecret}"));
            peticion.Headers.Authorization = new AuthenticationHeaderValue("Basic", credencial);

            using var respuesta = await http.SendAsync(peticion, limite.Token);
            var codigo = (int)respuesta.StatusCode;

            if (!respuesta.IsSuccessStatusCode)
            {
                var detalle = await LeerErrorAsync(respuesta, ct);
                logger.LogError("MobiControl rechazó el token de {Empresa} ({Codigo}): {Detalle}", config.TenantNombre, codigo, detalle);
                return (null, new ResultadoIntegracion(AccionToken, false, codigo, detalle));
            }

            var contenido = await respuesta.Content.ReadFromJsonAsync<RespuestaToken>(ct);
            if (string.IsNullOrWhiteSpace(contenido?.AccessToken))
                return (null, new ResultadoIntegracion(
                    AccionToken, false, codigo, "La respuesta no trajo access_token."));

            // Un minuto de colchón para no usar un token que caduca en pleno viaje.
            Tokens[empresaId] = new TokenEnCache(
                contenido.AccessToken,
                DateTime.UtcNow.AddSeconds(Math.Max(60, contenido.ExpiresIn) - 60));

            return (contenido.AccessToken, new ResultadoIntegracion(AccionToken, true, codigo, null));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error obteniendo el token de MobiControl para {Empresa}.", config.TenantNombre);
            return (null, new ResultadoIntegracion(AccionToken, false, null, ex.Message));
        }
        finally
        {
            Candado.Release();
        }
    }

    private async Task<ResultadoIntegracion> EnviarAsync(
        int empresaId, ConfiguracionEmpresa config, HttpMethod metodo, string ruta, object cuerpo,
        string token, string accion, CancellationToken ct)
    {
        try
        {
            using var limite = ConLimite(config, ct);
            using var peticion = new HttpRequestMessage(metodo, Ruta(config, ruta))
            {
                Content = new StringContent(JsonSerializer.Serialize(cuerpo), Encoding.UTF8, "application/json"),
            };
            peticion.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var respuesta = await http.SendAsync(peticion, limite.Token);
            var codigo = (int)respuesta.StatusCode;

            if (respuesta.IsSuccessStatusCode)
                return new ResultadoIntegracion(accion, true, codigo, null);

            // Un token revocado antes de tiempo se ve como 401: se descarta el de la caché para
            // que la siguiente llamada pida uno nuevo en vez de repetir el mismo error.
            if (codigo == 401) Tokens.TryRemove(empresaId, out _);

            var detalle = await LeerErrorAsync(respuesta, ct);
            logger.LogError("MobiControl falló en {Accion} para {Empresa} ({Codigo}): {Detalle}", accion, config.TenantNombre, codigo, detalle);
            return new ResultadoIntegracion(accion, false, codigo, detalle);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error llamando a MobiControl en {Accion}.", accion);
            return new ResultadoIntegracion(accion, false, null, ex.Message);
        }
    }

    private static async Task<string> LeerErrorAsync(HttpResponseMessage respuesta, CancellationToken ct)
    {
        var texto = await respuesta.Content.ReadAsStringAsync(ct);
        texto = texto.Trim();
        return texto.Length > 900 ? texto[..900] : texto;
    }

    private record RespuestaToken(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}
