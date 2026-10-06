using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Geomonitoreo.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Inicial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Empresas",
                columns: table => new
                {
                    EmpresaId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OneTenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OneSlug = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Nombre = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    OneApiKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    OneApiSecret = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Activo = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    FechaCreacion = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    FechaActualizacion = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Empresas", x => x.EmpresaId);
                });

            migrationBuilder.CreateTable(
                name: "Geocercas",
                columns: table => new
                {
                    GeocercaId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EmpresaId = table.Column<int>(type: "int", nullable: false),
                    GeocercaUid = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    Nombre = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Descripcion = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Tipo = table.Column<string>(type: "varchar(20)", nullable: false),
                    Latitud = table.Column<double>(type: "float", nullable: false),
                    Longitud = table.Column<double>(type: "float", nullable: false),
                    RadioMetros = table.Column<double>(type: "float", nullable: true),
                    Vertices = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Color = table.Column<string>(type: "varchar(7)", nullable: false, defaultValue: "#0ea5e9"),
                    Activa = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    CreadaPor = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    FechaCreacion = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    FechaActualizacion = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Geocercas", x => x.GeocercaId);
                    table.CheckConstraint("CK_Geocercas_Tipo", "[Tipo] IN ('CIRCULO','POLIGONO')");
                    table.CheckConstraint("CK_Geocercas_Vertices", "[Vertices] IS NULL OR ISJSON([Vertices]) = 1");
                    table.ForeignKey(
                        name: "FK_Geocercas_Empresas_EmpresaId",
                        column: x => x.EmpresaId,
                        principalTable: "Empresas",
                        principalColumn: "EmpresaId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Empresas_OneTenantId",
                table: "Empresas",
                column: "OneTenantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Geocercas_EmpresaId",
                table: "Geocercas",
                column: "EmpresaId");

            migrationBuilder.CreateIndex(
                name: "IX_Geocercas_GeocercaUid",
                table: "Geocercas",
                column: "GeocercaUid",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Geocercas");

            migrationBuilder.DropTable(
                name: "Empresas");
        }
    }
}
