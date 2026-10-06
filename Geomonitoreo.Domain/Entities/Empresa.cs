namespace Geomonitoreo.Domain.Entities;

/// <summary>
/// Vínculo con un tenant de One. Geomonitoreo no es dueño de ninguna configuración: la consola de
/// MobiControl de cada empresa vive en One, como variables de la app "geomonitoreo", y se pide a
/// su API de integración con la credencial que se guarda aquí.
///
/// La fila aparece sola la primera vez que entra alguien de la empresa; lo único que se carga a
/// mano (en Vínculos) es la credencial de One.
/// </summary>
public class Empresa
{
    public int EmpresaId { get; set; }

    /// <summary>Tenant de One dueño de estos datos.</summary>
    public Guid OneTenantId { get; set; }

    /// <summary>Slug del tenant, copiado de One para mostrarlo sin ir a preguntar.</summary>
    public string OneSlug { get; set; } = string.Empty;

    /// <summary>Nombre del tenant, copia de conveniencia. La fuente de verdad es One.</summary>
    public string Nombre { get; set; } = string.Empty;

    /// <summary>Api key emitida por One para el par tenant–geomonitoreo.</summary>
    public string? OneApiKey { get; set; }

    /// <summary>Secreto de esa credencial. Nunca sale de este servidor.</summary>
    public string? OneApiSecret { get; set; }

    public bool OneConfigurado =>
        !string.IsNullOrWhiteSpace(OneApiKey) && !string.IsNullOrWhiteSpace(OneApiSecret);

    public bool Activo { get; set; } = true;

    public DateTime FechaCreacion { get; set; }
    public DateTime? FechaActualizacion { get; set; }
}

/// <summary>Contrato de lo que pertenece a una empresa. Lo usa el filtro global del contexto.</summary>
public interface IDeEmpresa
{
    int EmpresaId { get; set; }
    Empresa Empresa { get; set; }
}
