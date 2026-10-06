using Microsoft.EntityFrameworkCore;
using Geomonitoreo.Application.Common.Interfaces;
using Geomonitoreo.Domain.Entities;

namespace Geomonitoreo.Infrastructure.Persistence;

/// <summary>
/// Base propia de Geomonitoreo. Guarda poco a propósito: los equipos, su estado y sus recorridos
/// los tiene MobiControl; las empresas y sus credenciales, One. Aquí solo queda el vínculo con
/// One y lo que es de Geomonitoreo, como las geocercas.
/// </summary>
public class ApplicationDbContext(
    DbContextOptions<ApplicationDbContext> options,
    IContextoEmpresa? contextoEmpresa = null)
    : DbContext(options), IApplicationDbContext
{
    // Se leen en cada consulta, no al construir el contexto: la empresa se resuelve cuando ya se
    // autenticó la petición, que puede ser después de que el contexto exista.
    private int? EmpresaFiltro => contextoEmpresa?.EmpresaId;

    // Sin contexto de empresa —herramientas de EF, migraciones— no se filtra nada; con
    // administrador de plataforma sin empresa elegida tampoco.
    private bool SinFiltro => contextoEmpresa is null || contextoEmpresa.EsSuperAdministrador;

    public DbSet<Empresa> Empresas => Set<Empresa>();
    public DbSet<Geocerca> Geocercas => Set<Geocerca>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Empresa>(e =>
        {
            e.ToTable("Empresas");
            e.HasKey(x => x.EmpresaId);
            e.HasIndex(x => x.OneTenantId).IsUnique();
            e.Property(x => x.OneSlug).HasMaxLength(100).IsRequired();
            e.Property(x => x.Nombre).HasMaxLength(200).IsRequired();
            e.Property(x => x.OneApiKey).HasMaxLength(200);
            e.Property(x => x.OneApiSecret).HasMaxLength(300);
            e.Property(x => x.Activo).HasDefaultValue(true);
            e.Property(x => x.FechaCreacion).HasDefaultValueSql("SYSUTCDATETIME()");
            e.Ignore(x => x.OneConfigurado);
        });

        builder.Entity<Geocerca>(e =>
        {
            e.ToTable("Geocercas", t =>
            {
                t.HasCheckConstraint("CK_Geocercas_Tipo", "[Tipo] IN ('CIRCULO','POLIGONO')");
                t.HasCheckConstraint("CK_Geocercas_Vertices", "[Vertices] IS NULL OR ISJSON([Vertices]) = 1");
            });

            e.HasKey(x => x.GeocercaId);
            e.Property(x => x.GeocercaUid).HasDefaultValueSql("NEWID()");
            e.HasIndex(x => x.GeocercaUid).IsUnique();

            e.Property(x => x.Nombre).HasMaxLength(100).IsRequired();
            e.Property(x => x.Descripcion).HasMaxLength(500);
            e.Property(x => x.Tipo).HasConversion<string>().HasColumnType("varchar(20)");
            e.Property(x => x.Vertices).HasColumnType("nvarchar(max)");
            e.Property(x => x.Color).HasColumnType("varchar(7)").HasDefaultValue("#0ea5e9");
            e.Property(x => x.Activa).HasDefaultValue(true);
            e.Property(x => x.CreadaPor).HasMaxLength(200);
            e.Property(x => x.FechaCreacion).HasDefaultValueSql("SYSUTCDATETIME()");
        });

        // ---------------- Aislamiento ----------------
        // Una sola vez para todo lo que implementa IDeEmpresa: la columna, la llave foránea y el
        // filtro. Hacerlo entidad por entidad invita a que la próxima quede fuera.
        ConfigurarPorEmpresa<Geocerca>(builder);
    }

    private void ConfigurarPorEmpresa<T>(ModelBuilder builder) where T : class, IDeEmpresa
    {
        builder.Entity<T>(e =>
        {
            e.Property(x => x.EmpresaId).IsRequired();
            e.HasOne(x => x.Empresa).WithMany().HasForeignKey(x => x.EmpresaId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.EmpresaId);
            e.HasQueryFilter(x => SinFiltro || x.EmpresaId == EmpresaFiltro);
        });
    }
}
