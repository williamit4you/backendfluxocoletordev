using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowTrack.Data.Migrations
{
    /// <inheritdoc />
    public partial class StepExecutionDataJson : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DataJson",
                table: "StepExecutions",
                type: "text",
                nullable: false,
                defaultValue: "{}");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DataJson",
                table: "StepExecutions");
        }
    }
}
