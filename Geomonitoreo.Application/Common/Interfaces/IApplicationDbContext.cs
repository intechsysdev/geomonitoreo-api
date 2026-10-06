using Microsoft.EntityFrameworkCore;
using Geomonitoreo.Domain.Entities;

namespace Geomonitoreo.Application.Common.Interfaces;

/// <summary>Abstracción del contexto de datos usada por la capa de aplicación.</summary>
public interface IApplicationDbContext
{
    DbSet<Empresa> Empresas { get; }
    DbSet<Geocerca> Geocercas { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
