using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using FlowTrack.Data;

#nullable disable

namespace FlowTrack.Data.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260624120000_ListFieldStructuredOptions")]
    public partial class ListFieldStructuredOptions : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Key",
                table: "StepFieldOptions",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Mask",
                table: "StepFieldOptions",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Required",
                table: "StepFieldOptions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "Type",
                table: "StepFieldOptions",
                type: "integer",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Key",
                table: "StepFieldOptions");

            migrationBuilder.DropColumn(
                name: "Mask",
                table: "StepFieldOptions");

            migrationBuilder.DropColumn(
                name: "Required",
                table: "StepFieldOptions");

            migrationBuilder.DropColumn(
                name: "Type",
                table: "StepFieldOptions");
        }
    }
}
