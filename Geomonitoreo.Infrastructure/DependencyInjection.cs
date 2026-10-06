using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Geomonitoreo.Application.Common.Interfaces;
using Geomonitoreo.Application.Dispositivos;
using Geomonitoreo.Application.Flota;
using Geomonitoreo.Application.Geocercas;
using Geomonitoreo.Infrastructure.MobiControl;
using Geomonitoreo.Infrastructure.One;
using Geomonitoreo.Infrastructure.Persistence;

namespace Geomonitoreo.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // ---- Base de datos (SQL Server) ----
        var cadenaConexion = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Falta la cadena de conexión 'Default'.");

        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseSqlServer(cadenaConexion, sql =>
                sql.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName)));

        services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<ApplicationDbContext>());

        services.AddMemoryCache();

        // ---- MobiControl ----
        // Sin BaseAddress: cada empresa apunta a su propia consola, así que el cliente compone la
        // URL completa en cada llamada. El plazo real lo pone cada llamada; este es solo un tope.
        services.AddHttpClient<IClienteMobiControl, ClienteMobiControl>(cliente =>
        {
            cliente.Timeout = TimeSpan.FromMinutes(2);
        });

        // ---- Configuración centralizada en One ----
        services.AddHttpClient<IProveedorConfiguracion, ProveedorConfiguracionOne>(cliente =>
        {
            var baseUrl = configuration["One:BaseUrl"];
            if (!string.IsNullOrWhiteSpace(baseUrl))
                cliente.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");

            cliente.Timeout = TimeSpan.FromSeconds(20);
        });

        // ---- Casos de uso ----
        services.AddScoped<IServicioFlota, ServicioFlota>();
        services.AddScoped<IServicioDispositivos, ServicioDispositivos>();
        services.AddScoped<IServicioGeocercas, ServicioGeocercas>();

        return services;
    }
}
