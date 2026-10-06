using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Geomonitoreo.Infrastructure.Persistence;

/// <summary>
/// Contexto para las herramientas de EF (<c>dotnet ef migrations</c>), que corren sin arrancar
/// la aplicación. Lee la misma cadena de conexión del API para no tener dos verdades.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var rutaApi = Path.GetFullPath(Path.Combine(
            Directory.GetCurrentDirectory(), "..", "Geomonitoreo.API"));

        // Mismo entorno que usa el host: sin variable, Development, igual que al depurar.
        // Antes se apilaba appsettings.Development.json siempre, así que `dotnet ef database
        // update` iba a la base local aunque se quisiera actualizar la de Azure.
        var entorno = PrimeroConValor(
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
            Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"))
            ?? "Development";

        var configuracion = new ConfigurationBuilder()
            .SetBasePath(Directory.Exists(rutaApi) ? rutaApi : Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{entorno}.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        // Sin cadena se reventaba antes contra un servidor local fijo: una migración lanzada
        // contra Azure terminaba escribiendo en la base del equipo sin avisar.
        var cadena = configuracion.GetConnectionString("Default")
            ?? throw new InvalidOperationException(
                $"Falta la cadena de conexión 'Default' para el entorno '{entorno}'. " +
                "Defínela en appsettings, en ConnectionStrings__Default, o pásala con --connection.");

        var opciones = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(cadena)
            .Options;

        return new ApplicationDbContext(opciones);
    }

    // Una variable definida pero vacía no es una elección de entorno: en shells y en el panel
    // del App Service es fácil dejarla en blanco, y sin esto se buscaría "appsettings..json".
    private static string? PrimeroConValor(params string?[] valores) =>
        valores.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
}
