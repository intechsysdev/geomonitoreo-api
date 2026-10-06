using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Geomonitoreo.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GeocercasEnMobiControl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ExisteEnMobiControl",
                table: "Geocercas",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FechaSincronizacion",
                table: "Geocercas",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReferenceIdMobiControl",
                table: "Geocercas",
                type: "varchar(64)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Geocercas_EmpresaId_Nombre",
                table: "Geocercas",
                columns: new[] { "EmpresaId", "Nombre" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Geocercas_EmpresaId_Nombre",
                table: "Geocercas");

            migrationBuilder.DropColumn(
                name: "ExisteEnMobiControl",
                table: "Geocercas");

            migrationBuilder.DropColumn(
                name: "FechaSincronizacion",
                table: "Geocercas");

            migrationBuilder.DropColumn(
                name: "ReferenceIdMobiControl",
                table: "Geocercas");
        }
    }
}
