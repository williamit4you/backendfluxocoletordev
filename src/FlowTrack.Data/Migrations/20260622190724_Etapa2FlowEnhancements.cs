using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowTrack.Data.Migrations
{
    /// <inheritdoc />
    public partial class Etapa2FlowEnhancements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:entry_type", "manual,reader,automatic,api_send,api_query")
                .Annotation("Npgsql:Enum:field_type", "text,number,date,document,email,select")
                .Annotation("Npgsql:Enum:instance_status", "in_progress,completed,cancelled")
                .Annotation("Npgsql:Enum:step_status", "pending,in_progress,completed,failed")
                .Annotation("Npgsql:Enum:step_type", "reader,user_task,external_monitor,automatic,api_send,api_query")
                .Annotation("Npgsql:Enum:token_type", "bearer,api_key")
                .Annotation("Npgsql:Enum:user_role", "super_admin,admin,user")
                .OldAnnotation("Npgsql:Enum:entry_type", "manual,reader,automatic")
                .OldAnnotation("Npgsql:Enum:field_type", "text,number,date,document,email,select")
                .OldAnnotation("Npgsql:Enum:instance_status", "in_progress,completed,cancelled")
                .OldAnnotation("Npgsql:Enum:step_status", "pending,in_progress,completed,failed")
                .OldAnnotation("Npgsql:Enum:step_type", "reader,user_task,external_monitor,automatic")
                .OldAnnotation("Npgsql:Enum:user_role", "super_admin,admin,user");

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "FlowSteps",
                type: "character varying(160)",
                maxLength: 160,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.CreateTable(
                name: "FlowTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FlowDefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Value = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    HeaderName = table.Column<string>(type: "text", nullable: true),
                    Active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FlowTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FlowTokens_FlowDefinitions_FlowDefinitionId",
                        column: x => x.FlowDefinitionId,
                        principalTable: "FlowDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StepFields",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FlowStepId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Label = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Required = table.Column<bool>(type: "boolean", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StepFields", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StepFields_FlowSteps_FlowStepId",
                        column: x => x.FlowStepId,
                        principalTable: "FlowSteps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StepFieldOptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StepFieldId = table.Column<Guid>(type: "uuid", nullable: false),
                    Label = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Value = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StepFieldOptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StepFieldOptions_StepFields_StepFieldId",
                        column: x => x.StepFieldId,
                        principalTable: "StepFields",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FlowTokens_FlowDefinitionId_Name",
                table: "FlowTokens",
                columns: new[] { "FlowDefinitionId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StepFieldOptions_StepFieldId_Order",
                table: "StepFieldOptions",
                columns: new[] { "StepFieldId", "Order" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StepFields_FlowStepId_Key",
                table: "StepFields",
                columns: new[] { "FlowStepId", "Key" },
                unique: true);

            migrationBuilder.Sql("""CREATE EXTENSION IF NOT EXISTS pgcrypto;""");

            migrationBuilder.Sql(
                """
                INSERT INTO "StepFields" ("Id", "FlowStepId", "Key", "Label", "Type", "Required", "Order")
                SELECT gen_random_uuid(),
                       s."Id",
                       f."Key",
                       f."Label",
                       f."Type",
                       f."Required",
                       f."Order"
                FROM "FlowFields" f
                JOIN LATERAL (
                    SELECT fs."Id"
                    FROM "FlowSteps" fs
                    WHERE fs."FlowDefinitionId" = f."FlowDefinitionId"
                    ORDER BY fs."Order"
                    LIMIT 1
                ) s ON TRUE;
                """);

            migrationBuilder.DropTable(
                name: "FlowFields");

            migrationBuilder.DropColumn(
                name: "EntryType",
                table: "FlowDefinitions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FlowTokens");

            migrationBuilder.DropTable(
                name: "StepFieldOptions");

            migrationBuilder.DropTable(
                name: "StepFields");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:entry_type", "manual,reader,automatic")
                .Annotation("Npgsql:Enum:field_type", "text,number,date,document,email,select")
                .Annotation("Npgsql:Enum:instance_status", "in_progress,completed,cancelled")
                .Annotation("Npgsql:Enum:step_status", "pending,in_progress,completed,failed")
                .Annotation("Npgsql:Enum:step_type", "reader,user_task,external_monitor,automatic")
                .Annotation("Npgsql:Enum:user_role", "super_admin,admin,user")
                .OldAnnotation("Npgsql:Enum:entry_type", "manual,reader,automatic,api_send,api_query")
                .OldAnnotation("Npgsql:Enum:field_type", "text,number,date,document,email,select")
                .OldAnnotation("Npgsql:Enum:instance_status", "in_progress,completed,cancelled")
                .OldAnnotation("Npgsql:Enum:step_status", "pending,in_progress,completed,failed")
                .OldAnnotation("Npgsql:Enum:step_type", "reader,user_task,external_monitor,automatic,api_send,api_query")
                .OldAnnotation("Npgsql:Enum:token_type", "bearer,api_key")
                .OldAnnotation("Npgsql:Enum:user_role", "super_admin,admin,user");

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "FlowSteps",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(160)",
                oldMaxLength: 160);

            migrationBuilder.AddColumn<int>(
                name: "EntryType",
                table: "FlowDefinitions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "FlowFields",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FlowDefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "text", nullable: false),
                    Label = table.Column<string>(type: "text", nullable: false),
                    OptionsJson = table.Column<string>(type: "text", nullable: true),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    Required = table.Column<bool>(type: "boolean", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false)
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

            migrationBuilder.CreateIndex(
                name: "IX_FlowFields_FlowDefinitionId_Key",
                table: "FlowFields",
                columns: new[] { "FlowDefinitionId", "Key" },
                unique: true);
        }
    }
}
