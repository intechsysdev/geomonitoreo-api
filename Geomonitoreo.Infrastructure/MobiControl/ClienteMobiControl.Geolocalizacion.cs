using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Geomonitoreo.Application.Common;
using Geomonitoreo.Application.Common.Interfaces;

namespace Geomonitoreo.Infrastructure.MobiControl;

/// <summary>
/// Lecturas de la flota: equipos, última posición y recorrido, y la acción de localizar.
///
/// Las respuestas se leen campo por campo sobre el JSON y no con clases: MobiControl devuelve un
/// tipo distinto por plataforma (Android, iOS, Mac, Windows), cada uno con sus propios campos, y
/// aquí solo interesan unos pocos que todos comparten.
/// </summary>
public partial class ClienteMobiControl
{
    /// <summary>Tope de la paginación: 20 páginas de 500 son 10 000 equipos, más que cualquier flota real.</summary>
    private const int TamanoPagina = 500;
    private const int PaginasMaximas = 20;

    public async Task<IReadOnlyList<EquipoMobiControl>> ListarEquiposAsync(CancellationToken ct = default)
    {
        var (config, empresaId, token) = await SesionAsync(ct);
        var equipos = new List<EquipoMobiControl>();

        for (var pagina = 0; pagina < PaginasMaximas; pagina++)
        {
            using var json = await LeerAsync(empresaId, config, token,
                $"api/devices?skip={pagina * TamanoPagina}&take={TamanoPagina}", null, ct);

            if (json is null || json.RootElement.ValueKind != JsonValueKind.Array) break;

            foreach (var equipo in json.RootElement.EnumerateArray())
            {
                var deviceId = Texto(equipo, "DeviceId");
                if (deviceId is null) continue;

                equipos.Add(Equipo(equipo, deviceId));
            }

            if (json.RootElement.GetArrayLength() < TamanoPagina) break;
        }

        return equipos;
    }

    public async Task<EquipoMobiControl?> ObtenerEquipoAsync(string deviceId, CancellationToken ct = default)
    {
        var (config, empresaId, token) = await SesionAsync(ct);

        using var json = await LeerAsync(empresaId, config, token,
            $"api/devices/{Uri.EscapeDataString(deviceId)}", null, ct);

        if (json is null || json.RootElement.ValueKind != JsonValueKind.Object) return null;
        return Texto(json.RootElement, "DeviceId") is { } id ? Equipo(json.RootElement, id) : null;
    }

    public async Task<UbicacionMobiControl?> UltimaUbicacionAsync(string deviceId, CancellationToken ct = default)
    {
        var (config, empresaId, token) = await SesionAsync(ct);

        // Límite corto: se pide para toda la flota en paralelo, y un equipo que no contesta no
        // puede demorar el mapa entero. Sin posición sale sin punto, no con error.
        using var json = await LeerAsync(empresaId, config, token,
            $"api/devices/{Uri.EscapeDataString(deviceId)}/lastKnownLocation", TimeSpan.FromSeconds(12), ct);

        if (json is null || json.RootElement.ValueKind != JsonValueKind.Object) return null;
        return Ubicacion(json.RootElement, "CollectionTimestamp");
    }

    public async Task<IReadOnlyList<UbicacionMobiControl>> RecorridoAsync(
        string deviceId, DateTimeOffset desde, DateTimeOffset hasta, CancellationToken ct = default)
    {
        var (config, empresaId, token) = await SesionAsync(ct);

        var ruta = $"api/devices/{Uri.EscapeDataString(deviceId)}/collectedData" +
                   $"?startDate={Uri.EscapeDataString(desde.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture))}" +
                   $"&endDate={Uri.EscapeDataString(hasta.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture))}" +
                   "&builtInDataType=Location";

        using var json = await LeerAsync(empresaId, config, token, ruta, null, ct);
        if (json is null || json.RootElement.ValueKind != JsonValueKind.Array) return [];

        return
        [
            .. json.RootElement.EnumerateArray()
                .Select(punto => Ubicacion(punto, "Timestamp"))
                .OfType<UbicacionMobiControl>()
                .OrderBy(punto => punto.Momento)
        ];
    }

    public async Task LocalizarAsync(string deviceId, CancellationToken ct = default)
    {
        var (config, empresaId, token) = await SesionAsync(ct);

        var resultado = await EnviarAsync(
            empresaId, config, HttpMethod.Post, $"api/devices/{Uri.EscapeDataString(deviceId)}/actions",
            new { Action = "Locate" }, token, "LOCALIZAR", ct);

        if (!resultado.Exitoso)
            throw new ErrorSolicitudException(
                $"MobiControl no aceptó la solicitud de ubicación ({resultado.CodigoHttp?.ToString() ?? "sin respuesta"}): {resultado.MensajeError}");
    }

    // ------------------------------------------------------------------------------------

    private async Task<(ConfiguracionEmpresa Config, int EmpresaId, string Token)> SesionAsync(CancellationToken ct)
    {
        var config = await ConfigAsync(ct)
            ?? throw new ErrorSolicitudException(
                "No se pudo obtener de One la configuración de la empresa. Revise la credencial de One en Vínculos.");

        if (!config.MobiControlConfigurado)
            throw new ErrorSolicitudException(
                $"{config.TenantNombre} no tiene configurada su consola de MobiControl en One (Geomonitoreo → Variables).");

        var empresaId = contextoEmpresa.EmpresaRequerida;
        var (token, resultado) = await ObtenerTokenAsync(empresaId, config, ct);

        // Con un usuario o una contraseña mal escritos la consola responde 400 o 500 sin cuerpo: el
        // detalle vacío no le dice nada a quien mira el mapa, así que se le indica qué revisar.
        if (token is null)
            throw new ErrorSolicitudException(string.IsNullOrWhiteSpace(resultado.MensajeError)
                ? $"MobiControl rechazó la conexión ({resultado.CodigoHttp?.ToString() ?? "sin respuesta"}). Revise en One " +
                  "(Geomonitoreo → Variables) el usuario, la contraseña, el client ID y el client secret de la consola."
                : $"MobiControl rechazó la conexión: {resultado.MensajeError}");

        return (config, empresaId, token);
    }

    /// <summary>GET con el token de la empresa. Null si el recurso no existe (404) o viene vacío.</summary>
    private async Task<JsonDocument?> LeerAsync(
        int empresaId, ConfiguracionEmpresa config, string token, string ruta, TimeSpan? limite, CancellationToken ct)
    {
        using var fuente = CancellationTokenSource.CreateLinkedTokenSource(ct);
        fuente.CancelAfter(limite ?? TimeSpan.FromSeconds(Math.Max(5, config.MobiControlTimeoutSegundos)));

        using var peticion = new HttpRequestMessage(HttpMethod.Get, Ruta(config, ruta));
        peticion.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        peticion.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage respuesta;
        try
        {
            respuesta = await http.SendAsync(peticion, fuente.Token);
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

        using (respuesta)
        {
            if (respuesta.StatusCode == HttpStatusCode.NotFound) return null;

            // MobiControl responde 403, y no 404, cuando el equipo no existe en la consola: no
            // distingue "no está" de "no puedes verlo".
            if (respuesta.StatusCode == HttpStatusCode.Forbidden)
                throw new ErrorSolicitudException(
                    "MobiControl no reconoce ese equipo, o el usuario de la API no tiene permiso sobre su carpeta.");

            if (!respuesta.IsSuccessStatusCode)
            {
                if (respuesta.StatusCode == HttpStatusCode.Unauthorized) Tokens.TryRemove(empresaId, out _);

                var detalle = await LeerErrorAsync(respuesta, ct);
                logger.LogError("MobiControl falló en {Ruta} para {Empresa} ({Codigo}): {Detalle}",
                    ruta, config.TenantNombre, (int)respuesta.StatusCode, detalle);
                throw new ErrorSolicitudException($"MobiControl respondió {(int)respuesta.StatusCode}: {detalle}");
            }

            var texto = await respuesta.Content.ReadAsStringAsync(ct);
            return string.IsNullOrWhiteSpace(texto) ? null : JsonDocument.Parse(texto);
        }
    }

    /// <summary>Los campos que se usan de un equipo. Son comunes a todas las plataformas.</summary>
    private static EquipoMobiControl Equipo(JsonElement equipo, string deviceId) => new(
        deviceId,
        Texto(equipo, "DeviceName") ?? deviceId,
        Plataforma(Texto(equipo, "$type") ?? Texto(equipo, "Platform")),
        Texto(equipo, "Manufacturer"),
        Texto(equipo, "Model"),
        Logico(equipo, "IsAgentOnline") ?? false,
        Entero(equipo, "BatteryStatus") is { } bateria and >= 0 and <= 100 ? bateria : null,
        Logico(equipo, "IsCharging"),
        Fecha(equipo, "LastCheckInTime") ?? Fecha(equipo, "LastAgentConnectTime"),
        Texto(equipo, "Path"),
        Texto(equipo, "IMEI_MEID_ESN"),
        Texto(equipo, "PhoneNumber"),
        Texto(equipo, "HardwareSerialNumber"),
        Texto(equipo, "OSVersion"),
        Texto(equipo, "AgentVersion"),
        Fecha(equipo, "EnrollmentTime"),
        Texto(equipo, "CellularCarrier") ?? Texto(equipo, "NetworkConnectionType"));

    /// <summary>MobiControl no manda la plataforma sola: sale del tipo polimórfico ("DeviceAndroidForWork", "DeviceIos"…).</summary>
    private static string? Plataforma(string? tipo) => tipo switch
    {
        null => null,
        _ when tipo.Contains("Android", StringComparison.OrdinalIgnoreCase) => "Android",
        _ when tipo.Contains("Ios", StringComparison.OrdinalIgnoreCase) => "iOS",
        _ when tipo.Contains("Mac", StringComparison.OrdinalIgnoreCase) => "macOS",
        _ when tipo.Contains("Windows", StringComparison.OrdinalIgnoreCase) => "Windows",
        _ when tipo.Contains("Linux", StringComparison.OrdinalIgnoreCase) => "Linux",
        _ => tipo.StartsWith("Device", StringComparison.Ordinal) ? tipo["Device".Length..] : tipo,
    };

    private static UbicacionMobiControl? Ubicacion(JsonElement punto, string campoMomento)
    {
        var latitud = Decimal(punto, "Latitude");
        var longitud = Decimal(punto, "Longitude");
        var momento = Fecha(punto, campoMomento);

        // (0, 0) es lo que devuelve un equipo que nunca obtuvo posición: un punto en el golfo de
        // Guinea no es una ubicación, es la ausencia de una.
        if (latitud is null || longitud is null || momento is null) return null;
        if (Math.Abs(latitud.Value) < 0.0001 && Math.Abs(longitud.Value) < 0.0001) return null;

        return new UbicacionMobiControl(latitud.Value, longitud.Value, momento.Value,
            Decimal(punto, "Speed"), Decimal(punto, "Heading"));
    }

    private static string? Texto(JsonElement objeto, string campo) =>
        objeto.TryGetProperty(campo, out var valor) && valor.ValueKind == JsonValueKind.String
            ? (string.IsNullOrWhiteSpace(valor.GetString()) ? null : valor.GetString())
            : null;

    private static bool? Logico(JsonElement objeto, string campo) =>
        objeto.TryGetProperty(campo, out var valor) && valor.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? valor.GetBoolean()
            : null;

    private static int? Entero(JsonElement objeto, string campo) =>
        objeto.TryGetProperty(campo, out var valor) && valor.ValueKind == JsonValueKind.Number && valor.TryGetInt32(out var n)
            ? n
            : null;

    private static double? Decimal(JsonElement objeto, string campo) =>
        objeto.TryGetProperty(campo, out var valor) && valor.ValueKind == JsonValueKind.Number ? valor.GetDouble() : null;

    /// <summary>Las fechas llegan en ISO 8601; sin zona explícita se toman como UTC, que es como las guarda la consola.</summary>
    private static DateTimeOffset? Fecha(JsonElement objeto, string campo) =>
        Texto(objeto, campo) is { } texto &&
        DateTimeOffset.TryParse(texto, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var fecha) &&
        fecha.Year > 2000
            ? fecha
            : null;
}
