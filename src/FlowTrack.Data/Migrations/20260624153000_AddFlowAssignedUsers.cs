using FlowTrack.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowTrack.Data.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260624153000_AddFlowAssignedUsers")]
    public partial class AddFlowAssignedUsers : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FlowDefinitionUsers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FlowDefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FlowDefinitionUsers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FlowDefinitionUsers_FlowDefinitions_FlowDefinitionId",
                        column: x => x.FlowDefinitionId,
                        principalTable: "FlowDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FlowDefinitionUsers_FlowDefinitionId_UserId",
                table: "FlowDefinitionUsers",
                columns: new[] { "FlowDefinitionId", "UserId" },
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FlowDefinitionUsers");
        }
    }
}
