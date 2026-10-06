using System.Text.Encodings.Web;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using Geomonitoreo.API.Configuration;
using Geomonitoreo.Application.Common;
using Geomonitoreo.Application.Common.Interfaces;
using Geomonitoreo.Infrastructure;
using Geomonitoreo.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

const string PoliticaCors = "GeomonitoreoCors";

// --- Opciones ---
builder.Services.Configure<AppOptions>(builder.Configuration.GetSection(AppOptions.SectionName));
var appOptions = builder.Configuration.GetSection(AppOptions.SectionName).Get<AppOptions>() ?? new AppOptions();

// --- Controladores + JSON (enums como texto) ---
builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        o.JsonSerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
    });

// --- Infraestructura (base de datos, MobiControl, One) ---
builder.Services.AddInfrastructure(builder.Configuration);

// --- Identidad: la emite y la valida One ---
// Este API no tiene usuarios propios ni verifica firmas: reenvía el token a One y le pregunta
// quién es. Verificar la firma aquí exigiría compartir la llave con la que One firma todos los
// tokens de la plataforma.
builder.Services.Configure<OneOptions>(builder.Configuration.GetSection(OneOptions.SectionName));
var one = builder.Configuration.GetSection(OneOptions.SectionName).Get<OneOptions>() ?? new OneOptions();

if (builder.Environment.IsProduction() && !one.EstaConfigurado)
    throw new InvalidOperationException(
        "Falta 'One:BaseUrl'. Sin One no hay forma de autenticar usuarios ni de resolver la configuración de cada empresa.");

builder.Services.AddHttpClient(ManejadorAutenticacionOne.ClienteHttp, cliente =>
{
    if (!string.IsNullOrWhiteSpace(one.BaseUrl))
        cliente.BaseAddress = new Uri(one.BaseUrl.TrimEnd('/') + "/");

    cliente.Timeout = TimeSpan.FromSeconds(15);
});

builder.Services
    .AddAuthentication(ManejadorAutenticacionOne.Esquema)
    .AddScheme<AuthenticationSchemeOptions, ManejadorAutenticacionOne>(ManejadorAutenticacionOne.Esquema, _ => { });

builder.Services.AddAuthorization();

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IContextoEmpresa, ContextoEmpresa>();
builder.Services.AddScoped<EmpresasOne>();

// --- CORS ---
// La autenticación va por cabecera, no por cookie: con la lista vacía (desarrollo) se permite
// cualquier origen sin que un sitio ajeno gane nada.
builder.Services.AddCors(options => options.AddPolicy(PoliticaCors, politica =>
{
    if (appOptions.CorsOrigins.Length == 0)
        politica.AllowAnyOrigin();
    else
        politica.WithOrigins(appOptions.CorsOrigins);

    politica.AllowAnyHeader().AllowAnyMethod();
}));

// --- Cabeceras reenviadas ---
// App Service entrega la petición desde su propio frente: sin esto, la IP de todos sería la del
// proxy y el limitador metería a todo el mundo en la misma partición.
builder.Services.Configure<ForwardedHeadersOptions>(opciones =>
{
    opciones.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    opciones.KnownIPNetworks.Clear();
    opciones.KnownProxies.Clear();
});

// --- Límite de peticiones ---
builder.Services.AddRateLimiter(opciones =>
{
    opciones.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    opciones.OnRejected = async (contexto, ct) =>
    {
        contexto.HttpContext.Response.Headers.RetryAfter = "60";
        if (!contexto.HttpContext.Response.HasStarted)
            await contexto.HttpContext.Response.WriteAsJsonAsync(
                new { message = "Demasiadas peticiones seguidas. Espera un minuto y vuelve a intentarlo." }, ct);
    };

    static string Cliente(HttpContext contexto) => contexto.Connection.RemoteIpAddress?.ToString() ?? "desconocida";

    opciones.AddPolicy(PoliticasLimite.General, contexto =>
        RateLimitPartition.GetFixedWindowLimiter(Cliente(contexto), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 300,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));

    opciones.AddPolicy(PoliticasLimite.Acciones, contexto =>
        RateLimitPartition.GetFixedWindowLimiter(Cliente(contexto), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
});

// --- Swagger ---
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Geomonitoreo API",
        Version = "v1",
        Description = "Flota de MobiControl en el mapa: estado, ubicación, recorridos y geocercas. Autenticación con el token de Intechsys One.",
    });

    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        Description = "Token de acceso de Intechsys One.",
    });

    c.AddSecurityRequirement(documento => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference("Bearer", documento)] = [],
    });
});

var app = builder.Build();

// Lo primero: el resto del canal necesita la IP real del cliente.
app.UseForwardedHeaders();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    await db.Database.MigrateAsync();
}

app.UseSwagger();
app.UseSwaggerUI();

app.UseCors(PoliticaCors);

app.UseAuthentication();
app.UseAuthorization();

// Traduce el token de One a la empresa local. Después de autenticar, porque lee los claims, y
// antes de los controladores, porque el contexto de datos ya filtra con ello.
app.UseMiddleware<ResolucionTenantMiddleware>();

// Errores: los de validación salen como 400 con el mensaje tal cual, que el front muestra; el
// resto se registra y devuelve 500. Después de CORS, para que el navegador pueda leerlos.
app.Use(async (contexto, siguiente) =>
{
    try
    {
        await siguiente();
    }
    catch (OperationCanceledException) when (contexto.RequestAborted.IsCancellationRequested)
    {
        // El cliente se fue antes de la respuesta: no hay a quién contestarle.
    }
    catch (ErrorSolicitudException ex)
    {
        if (!contexto.Response.HasStarted)
        {
            contexto.Response.Clear();
            contexto.Response.StatusCode = StatusCodes.Status400BadRequest;
            await contexto.Response.WriteAsJsonAsync(new { message = ex.Message });
        }
    }
    catch (Exception ex)
    {
        contexto.RequestServices.GetRequiredService<ILogger<Program>>()
            .LogError(ex, "Error no controlado en {Ruta}", contexto.Request.Path);

        if (!contexto.Response.HasStarted)
        {
            contexto.Response.Clear();
            contexto.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await contexto.Response.WriteAsJsonAsync(new { message = "Error interno. Quedó registrado; inténtelo de nuevo." });
        }
    }
});

app.UseRateLimiter();

app.MapControllers().RequireRateLimiting(PoliticasLimite.General);

app.Run();
