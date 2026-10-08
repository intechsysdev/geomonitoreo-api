using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Geomonitoreo.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Las geocercas que llegaron de la consola de MobiControl se pintaban de morado; con la paleta
    /// de marca van en ámbar. Solo cambia las que siguen con el morado de entonces: si alguien les
    /// eligió un color, se respeta.
    /// </summary>
    public partial class GeocercasDeConsolaEnAmbar : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE Geocercas SET Color = '#da830b' WHERE CreadaPor = 'MobiControl' AND Color = '#a855f7';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE Geocercas SET Color = '#a855f7' WHERE CreadaPor = 'MobiControl' AND Color = '#da830b';");
        }
    }
}
