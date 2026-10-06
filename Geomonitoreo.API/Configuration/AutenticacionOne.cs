using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Geomonitoreo.API.Configuration;

/// <summary>
/// Valida el token del usuario preguntándole a One, en vez de verificar su firma aquí.
///
/// Verificar la firma exigiría compartir la llave con la que One firma *todos* los tokens de la
/// plataforma. Esa llave tendría que vivir en la configuración de este servicio, que es público:
/// quien la leyera podría fabricar tokens de cualquier usuario y cualquier empresa, incluido el
/// administrador de plataforma. Delegar la validación deja el secreto donde nació.
///
/// El costo es una llamada a One por petición, que se amortigua con una caché corta: lo que se
/// guarda es el resultado de validar un token concreto. One rechaza los tokens de una sesión ya
/// cerrada (al salir del portal se cierran también las apps abiertas desde él), así que la caché
/// es lo único que demora el cierre aquí: se deja en segundos.
/// </summary>
public class ManejadorAutenticacionOne(
    IOptionsMonitor<AuthenticationSchemeOptions> opciones,
    ILoggerFactory registro,
    UrlEncoder codificador,
    IHttpClientFactory clientes,
    IMemoryCache cache)
    : AuthenticationHandler<AuthenticationSchemeOptions>(opciones, registro, codificador)
{
    public const string Esquema = "One";

    /// <summary>Cliente HTTP con la URL de One ya configurada.</summary>
    public const string ClienteHttp = "one-auth";

    private static readonly TimeSpan Vigencia = TimeSpan.FromSeconds(15);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var cabecera = Request.Headers.Authorization.FirstOrDefault();

        if (string.IsNullOrWhiteSpace(cabecera) ||
            !cabecera.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var token = cabecera[7..].Trim();
        if (token.Length == 0) return AuthenticateResult.NoResult();

        // La caché se indexa por el resumen del token, no por el token: así no queda en memoria
        // una credencial completa que cualquier volcado dejaría a la vista.
        var clave = "one:me:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

        if (!cache.TryGetValue<PerfilOne>(clave, out var perfil) || perfil is null)
        {
            perfil = await ConsultarAsync(token);

            if (perfil is null)
                return AuthenticateResult.Fail("One no reconoce el token.");

            cache.Set(clave, perfil, Vigencia);
        }

        return AuthenticateResult.Success(
            new AuthenticationTicket(AConstruirPrincipal(perfil), Esquema));
    }

    private async Task<PerfilOne?> ConsultarAsync(string token)
    {
        try
        {
            var http = clientes.CreateClient(ClienteHttp);

            using var peticion = new HttpRequestMessage(HttpMethod.Get, "api/v1/auth/me");
            peticion.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var respuesta = await http.SendAsync(peticion);

            if (!respuesta.IsSuccessStatusCode)
            {
                Logger.LogDebug("One rechazó el token ({Codigo}).", (int)respuesta.StatusCode);
                return null;
            }

            return await respuesta.Content.ReadFromJsonAsync<PerfilOne>();
        }
        catch (Exception ex)
        {
            // Si One no responde nadie puede entrar a la consola. Se registra como error porque
            // es una caída de dependencia, no una credencial inválida.
            Logger.LogError(ex, "No se pudo validar el token contra One.");
            return null;
        }
    }

    /// <summary>
    /// Arma la identidad con la misma forma que tendría el token de One: sus roles y un claim
    /// "tenant" por empresa, con formato "{tenantId}:{rol}". Así el resto del API no distingue
    /// de dónde vino la validación.
    /// </summary>
    private static ClaimsPrincipal AConstruirPrincipal(PerfilOne perfil)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, perfil.Id.ToString()),
            new(ClaimTypes.Email, perfil.Email),
            new(ClaimTypes.Name, perfil.FullName ?? perfil.Email),
        };

        foreach (var rol in perfil.Roles ?? [])
            claims.Add(new Claim(ClaimTypes.Role, rol));

        if (perfil.IsPlatformAdmin && !(perfil.Roles ?? []).Contains(One.RolPlataforma))
            claims.Add(new Claim(ClaimTypes.Role, One.RolPlataforma));

        foreach (var pertenencia in perfil.Memberships ?? [])
            claims.Add(new Claim(One.ClaimTenant, $"{pertenencia.TenantId}:{pertenencia.Role}"));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, Esquema, ClaimTypes.Email, ClaimTypes.Role));
    }

    private sealed record PerfilOne(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("email")] string Email,
        [property: JsonPropertyName("fullName")] string? FullName,
        [property: JsonPropertyName("isPlatformAdmin")] bool IsPlatformAdmin,
        [property: JsonPropertyName("roles")] IReadOnlyList<string>? Roles,
        [property: JsonPropertyName("memberships")] IReadOnlyList<PertenenciaOne>? Memberships);

    private sealed record PertenenciaOne(
        [property: JsonPropertyName("tenantId")] Guid TenantId,
        [property: JsonPropertyName("tenantName")] string? TenantName,
        [property: JsonPropertyName("tenantSlug")] string? TenantSlug,
        [property: JsonPropertyName("role")] string? Role);
}
