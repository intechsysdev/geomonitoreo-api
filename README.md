# geomonitoreo-api

API de **Geomonitoreo**: la flota de MobiControl de cada empresa en el mapa, con estado en vivo, recorridos y geocercas. Integrado con **Intechsys One**: no tiene usuarios propios ni guarda credenciales de MobiControl.

.NET 10 · ASP.NET Core · EF Core (SQL Server) · misma estructura que `firma-digital-api`:

```
Geomonitoreo.Domain          Empresa (vínculo con One) y Geocerca
Geomonitoreo.Application     Flota, Dispositivos (detalle y análisis de recorridos), Geocercas, geometría
Geomonitoreo.Infrastructure  EF Core, cliente de MobiControl, configuración desde One
Geomonitoreo.API             Controladores, autenticación con One, resolución de empresa
```

## Cómo se integra con One

| Qué | Dónde |
|---|---|
| Usuarios e inicio de sesión | One. El API valida cada token preguntándole a One (`/auth/me`, caché de 15 s), así que cerrar sesión en One también cierra aquí. |
| Empresas del usuario | One (`/me/apps/geomonitoreo/tenants`). La fila local de la empresa aparece en el primer ingreso. |
| Consola de MobiControl de cada empresa | Variables `MOBICONTROL_*` de la app **Geomonitoreo** en One, leídas con una credencial de One de la empresa (se carga en *Vínculos*). |
| Acceso desde "Mis aplicaciones" | SSO de One (`/sso` en el front). |

## Endpoints (`/api/v1`, con `Authorization: Bearer <token de One>` y `X-Tenant-Id`)

| Método | Ruta | |
|---|---|---|
| GET | `/sesion` | Quién es y a qué empresas alcanza |
| GET | `/flota` | Todos los equipos: estado, batería, última posición y geocercas en las que está (`?refrescar=true` ignora la caché de 25 s) |
| GET | `/flota/configuracion` | Si la empresa tiene MobiControl configurado |
| GET | `/dispositivos/{deviceId}` | Detalle del equipo |
| GET | `/dispositivos/{deviceId}/recorrido?desde&hasta` | Puntos, distancia, velocidad, paradas y entradas/salidas de geocercas (hasta 7 días) |
| POST | `/dispositivos/{deviceId}/localizar` | Pide la posición al equipo (límite de 10 por minuto) |
| GET/POST/PUT/DELETE | `/geocercas` | Zonas (círculo o polígono) |
| GET/PUT | `/vinculos` | Solo plataforma: credencial de One de cada empresa |
| GET | `/salud` | Sin sesión |

## Desarrollo

```bash
dotnet run --project Geomonitoreo.API --launch-profile http     # http://localhost:5240
```

`appsettings.Development.json` usa la base `geomonitoreo_db` del SQL Server local (JEFO-PC, autenticación de Windows) y el One de producción. La base y sus tablas se crean al arrancar.

## Despliegue

`.github/workflows/deploy.yml` publica en un App Service de Linux. Queda inactivo hasta configurar en el repositorio:

- Variable `AZURE_WEBAPP_NAME` y secreto `AZURE_WEBAPP_PUBLISH_PROFILE`.
- En el App Service: `ConnectionStrings__Default` (Azure SQL) y `App__CorsOrigins__0` (URL del front).
