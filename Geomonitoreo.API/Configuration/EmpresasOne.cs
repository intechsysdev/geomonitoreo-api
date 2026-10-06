using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Geomonitoreo.Domain.Entities;
using Geomonitoreo.Infrastructure.Persistence;

namespace Geomonitoreo.API.Configuration;

/// <summary>Empresa por la que el usuario llega a esta app, tal como la reporta One.</summary>
public sealed record EmpresaEnOne(
    [property: JsonPropertyName("tenantId")] Guid TenantId,
    [property: JsonPropertyName("name")] string Nombre,
    [property: JsonPropertyName("slug")] string Slug,
    [property: JsonPropertyName("role")] string? Rol);

/// <summary>
/// One es la fuente de verdad de a qué empresas llega cada usuario: las que tienen la app
/// "geomonitoreo" asignada y de las que el usuario es miembro. Aquí solo se le pregunta, con el
/// mismo token del usuario, y se mantiene al día la fila local de cada empresa.
///
/// La fila local hace falta —las geocercas cuelgan de ella, y guarda la credencial para leer la
/// configuración—, pero no la crea nadie a mano: aparece la primera vez que entra alguien de esa
/// empresa.
/// </summary>
public class EmpresasOne(
    IHttpClientFactory clientes,
    ApplicationDbContext db,
    IOptions<OneOptions> opciones,
    ILogger<EmpresasOne> logger)
{
    /// <summary>Null si One no respondió: quien llama decide qué hacer sin la lista.</summary>
    public async Task<IReadOnlyList<EmpresaEnOne>?> ConsultarAsync(string token, CancellationToken ct)
    {
        try
        {
            var http = clientes.CreateClient(ManejadorAutenticacionOne.ClienteHttp);
            var slug = Uri.EscapeDataString(opciones.Value.AppSlug);

            using var peticion = new HttpRequestMessage(HttpMethod.Get, $"api/v1/me/apps/{slug}/tenants");
            peticion.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var respuesta = await http.SendAsync(peticion, ct);

            if (!respuesta.IsSuccessStatusCode)
            {
                logger.LogWarning("One no devolvió las empresas del usuario ({Codigo}).", (int)respuesta.StatusCode);
                return null;
            }

            return await respuesta.Content.ReadFromJsonAsync<List<EmpresaEnOne>>(ct) ?? [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Incluye el límite del HttpClient (One lento o reiniciando): quien llama cae al
            // respaldo por las pertenencias del token. Solo se propaga si se fue el cliente.
            logger.LogError(ex, "No se pudieron consultar en One las empresas del usuario.");
            return null;
        }
    }

    /// <summary>
    /// Deja una fila local por cada empresa que reporta One y devuelve sus identificadores.
    /// Nombre y slug se refrescan siempre: la fuente de verdad es One.
    /// </summary>
    public async Task SincronizarAsync(IReadOnlyList<EmpresaEnOne> empresas, CancellationToken ct)
    {
        var ids = empresas.Select(e => e.TenantId).ToList();

        var existentes = await db.Empresas
            .Where(e => ids.Contains(e.OneTenantId))
            .ToDictionaryAsync(e => e.OneTenantId, ct);

        var nuevas = new List<Empresa>();

        foreach (var empresa in empresas)
        {
            if (existentes.TryGetValue(empresa.TenantId, out var local))
            {
                local.Nombre = empresa.Nombre;
                local.OneSlug = empresa.Slug;
                continue;
            }

            // Una empresa que se recreó en One conserva su slug pero cambia de identificador. Si
            // aquí ya había una con ese slug apuntando a un identificador que One no reporta, es
            // la misma: se re-apunta y conserva sus geocercas y su credencial, en vez de
            // abrir un compartimento vacío al lado. Los slugs son únicos en One, así que dos
            // empresas vivas nunca comparten uno.
            var huerfana = await db.Empresas
                .Where(e => e.OneSlug == empresa.Slug && !ids.Contains(e.OneTenantId))
                .ToListAsync(ct);

            if (huerfana.Count == 1)
            {
                logger.LogWarning(
                    "La empresa {Slug} cambió de tenant en One ({Anterior} → {Nuevo}); se re-apunta el vínculo.",
                    empresa.Slug, huerfana[0].OneTenantId, empresa.TenantId);

                huerfana[0].OneTenantId = empresa.TenantId;
                huerfana[0].Nombre = empresa.Nombre;
                huerfana[0].FechaActualizacion = DateTime.UtcNow;
                continue;
            }

            var nueva = new Empresa
            {
                OneTenantId = empresa.TenantId,
                OneSlug = empresa.Slug,
                Nombre = empresa.Nombre,
                Activo = true,
                FechaCreacion = DateTime.UtcNow,
            };

            db.Empresas.Add(nueva);
            nuevas.Add(nueva);
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Dos pestañas entrando a la vez pueden intentar crear la misma empresa; el índice
            // único sobre el tenant deja pasar una sola. La otra ya existe, que es lo que importa.
            logger.LogInformation(ex, "Otra petición creó primero una de las empresas; se continúa con la existente.");
            db.ChangeTracker.Clear();
            return;
        }

        foreach (var nueva in nuevas)
            logger.LogInformation("Empresa {Nombre} ({Tenant}) dada de alta desde One.", nueva.Nombre, nueva.OneTenantId);
    }
}
