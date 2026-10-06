namespace Geomonitoreo.Application.Common.Interfaces;

/// <summary>
/// Empresa a la que pertenece la petición en curso. Se resuelve una vez por petición, de la
/// sesión de One del usuario, y el contexto de datos filtra por ella sola.
///
/// Es a propósito que el aislamiento no dependa de que cada consulta recuerde filtrar: basta
/// olvidarlo una vez para que una empresa vea las zonas de otra.
/// </summary>
public interface IContextoEmpresa
{
    /// <summary>Empresa de la petición, o null si el usuario no ha elegido ninguna.</summary>
    int? EmpresaId { get; }

    /// <summary>Un administrador de plataforma sin empresa elegida ve todas.</summary>
    bool EsSuperAdministrador { get; }

    /// <summary>Empresa de la petición, reventando si no hay ninguna. Lo usa el código que escribe.</summary>
    int EmpresaRequerida { get; }

    /// <summary>Correo de quien hace la petición, para dejar constancia de quién creó qué.</summary>
    string? Usuario { get; }
}
