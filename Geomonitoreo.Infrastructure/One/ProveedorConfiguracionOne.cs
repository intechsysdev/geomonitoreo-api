using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Geomonitoreo.Application.Common.Interfaces;
using Geomonitoreo.Infrastructure.Persistence;

namespace Geomonitoreo.Infrastructure.One;

/// <summary>
/// Trae la configuración de cada empresa desde One, con la credencial que esa empresa tiene
/// emitida para la app "geomonitoreo". Se cachea unos minutos: cada vista del mapa la consulta,
/// y sin caché cada refresco sería una llamada a One antes de hacer nada útil.
/// </summary>
public class ProveedorConfiguracionOne(
    HttpClient http,
    ApplicationDbContext db,
    IMemoryCache cache,
    ILogger<ProveedorConfiguracionOne> logger) : IProveedorConfiguracion
{
    private static readonly TimeSpan Vigencia = TimeSpan.FromMinutes(5);

    private static string Clave(int empresaId) => $"one:config:{empresaId}";

    public void Olvidar(int empresaId) => cache.Remove(Clave(empresaId));

    public async Task<ConfiguracionEmpresa?> ObtenerAsync(int empresaId, CancellationToken ct = default)
    {
        if (cache.TryGetValue<ConfiguracionEmpresa>(Clave(empresaId), out var enCache) && enCache is not null)
            return enCache;

        var empresa = await db.Empresas.AsNoTracking().FirstOrDefaultAsync(e => e.EmpresaId == empresaId, ct);

        if (empresa is null || !empresa.OneConfigurado)
        {
            logger.LogWarning("La empresa {Empresa} no tiene credencial de One configurada.", empresaId);
            return null;
        }

        try
        {
            using var peticion = new HttpRequestMessage(HttpMethod.Get, "api/v1/integration/config");
            peticion.Headers.Add("X-Api-Key", empresa.OneApiKey);
            peticion.Headers.Add("X-Api-Secret", empresa.OneApiSecret);

            using var respuesta = await http.SendAsync(peticion, ct);

            if (!respuesta.IsSuccessStatusCode)
            {
                var detalle = await respuesta.Content.ReadAsStringAsync(ct);
                logger.LogError("One rechazó la configuración de {Empresa} ({Codigo}): {Detalle}",
                    empresa.Nombre, (int)respuesta.StatusCode, detalle[..Math.Min(400, detalle.Length)]);
                return null;
            }

            var cuerpo = await respuesta.Content.ReadFromJsonAsync<RespuestaOne>(ct);
            if (cuerpo is null) return null;

            var configuracion = Traducir(cuerpo);
            cache.Set(Clave(empresaId), configuracion, Vigencia);
            return configuracion;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Error consultando la configuración de {Empresa} en One.", empresa.Nombre);
            return null;
        }
    }

    /// <summary>Del diccionario plano de One a algo tipado; una variable no configurada llega ausente.</summary>
    private static ConfiguracionEmpresa Traducir(RespuestaOne cuerpo)
    {
        string? V(string clave) =>
            cuerpo.Settings.TryGetValue(clave, out var valor) && !string.IsNullOrWhiteSpace(valor)
                ? valor.Trim()
                : null;

        return new ConfiguracionEmpresa(
            TenantSlug: cuerpo.Tenant.Slug,
            TenantNombre: cuerpo.Tenant.Name,
            MobiControlBaseUrl: V("MOBICONTROL_BASE_URL"),
            MobiControlClientId: V("MOBICONTROL_CLIENT_ID"),
            MobiControlClientSecret: V("MOBICONTROL_CLIENT_SECRET"),
            MobiControlUsuario: V("MOBICONTROL_USUARIO"),
            MobiControlPassword: V("MOBICONTROL_PASSWORD"),
            MobiControlTimeoutSegundos: int.TryParse(V("MOBICONTROL_TIMEOUT_SEGUNDOS"), out var t) ? t : 25,
            ConfigVersion: cuerpo.ConfigVersion ?? string.Empty,
            GoogleMapsApiKey: V("GOOGLE_MAPS_API_KEY"),
            GoogleMapsMapId: V("GOOGLE_MAPS_MAP_ID"));
    }

    private sealed record RespuestaOne(
        [property: JsonPropertyName("tenant")] TenantOne Tenant,
        [property: JsonPropertyName("settings")] Dictionary<string, string?> Settings,
        [property: JsonPropertyName("configVersion")] string? ConfigVersion);

    private sealed record TenantOne(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("slug")] string Slug);
}
