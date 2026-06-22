using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowTrack.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:entry_type", "manual,reader,automatic")
                .Annotation("Npgsql:Enum:field_type", "text,number,date,document,email,select")
                .Annotation("Npgsql:Enum:instance_status", "in_progress,completed,cancelled")
                .Annotation("Npgsql:Enum:step_status", "pending,in_progress,completed,failed")
                .Annotation("Npgsql:Enum:step_type", "reader,user_task,external_monitor,automatic")
                .Annotation("Npgsql:Enum:user_role", "super_admin,admin,user");

            migrationBuilder.CreateTable(
                name: "AppUsers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Email = table.Column<string>(type: "text", nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: false),
                    Role = table.Column<int>(type: "integer", nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FlowDefinitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    EntryType = table.Column<int>(type: "integer", nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FlowDefinitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FlowFields",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FlowDefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "text", nullable: false),
                    Label = table.Column<string>(type: "text", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Required = table.Column<bool>(type: "boolean", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    OptionsJson = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FlowFields", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FlowFields_FlowDefinitions_FlowDefinitionId",
                        column: x => x.FlowDefinitionId,
                        principalTable: "FlowDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FlowInstances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FlowDefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    DataJson = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CurrentStepOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FlowInstances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FlowInstances_FlowDefinitions_FlowDefinitionId",
                        column: x => x.FlowDefinitionId,
                        principalTable: "FlowDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FlowSteps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FlowDefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    AssignedUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ConfigurationJson = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FlowSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FlowSteps_FlowDefinitions_FlowDefinitionId",
                        column: x => x.FlowDefinitionId,
                        principalTable: "FlowDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StepExecutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FlowInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    FlowStepId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Notes = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StepExecutions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StepExecutions_FlowInstances_FlowInstanceId",
                        column: x => x.FlowInstanceId,
                        principalTable: "FlowInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_StepExecutions_FlowSteps_FlowStepId",
                        column: x => x.FlowStepId,
                        principalTable: "FlowSteps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppUsers_Email",
                table: "AppUsers",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FlowFields_FlowDefinitionId_Key",
                table: "FlowFields",
                columns: new[] { "FlowDefinitionId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FlowInstances_FlowDefinitionId",
                table: "FlowInstances",
                column: "FlowDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_FlowSteps_FlowDefinitionId_Order",
                table: "FlowSteps",
                columns: new[] { "FlowDefinitionId", "Order" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StepExecutions_FlowInstanceId",
                table: "StepExecutions",
                column: "FlowInstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_StepExecutions_FlowStepId",
                table: "StepExecutions",
                column: "FlowStepId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppUsers");

            migrationBuilder.DropTable(
                name: "FlowFields");

            migrationBuilder.DropTable(
                name: "StepExecutions");

            migrationBuilder.DropTable(
                name: "FlowInstances");

            migrationBuilder.DropTable(
                name: "FlowSteps");

            migrationBuilder.DropTable(
                name: "FlowDefinitions");
        }
    }
}
