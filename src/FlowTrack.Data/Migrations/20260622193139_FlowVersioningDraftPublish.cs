using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowTrack.Data.Migrations
{
    /// <inheritdoc />
    public partial class FlowVersioningDraftPublish : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:entry_type", "manual,reader,automatic,api_send,api_query")
                .Annotation("Npgsql:Enum:field_type", "text,number,date,document,email,select")
                .Annotation("Npgsql:Enum:flow_lifecycle_status", "draft,published,archived")
                .Annotation("Npgsql:Enum:instance_status", "in_progress,completed,cancelled")
                .Annotation("Npgsql:Enum:step_status", "pending,in_progress,completed,failed")
                .Annotation("Npgsql:Enum:step_type", "reader,user_task,external_monitor,automatic,api_send,api_query")
                .Annotation("Npgsql:Enum:token_type", "bearer,api_key")
                .Annotation("Npgsql:Enum:user_role", "super_admin,admin,user")
                .OldAnnotation("Npgsql:Enum:entry_type", "manual,reader,automatic,api_send,api_query")
                .OldAnnotation("Npgsql:Enum:field_type", "text,number,date,document,email,select")
                .OldAnnotation("Npgsql:Enum:instance_status", "in_progress,completed,cancelled")
                .OldAnnotation("Npgsql:Enum:step_status", "pending,in_progress,completed,failed")
                .OldAnnotation("Npgsql:Enum:step_type", "reader,user_task,external_monitor,automatic,api_send,api_query")
                .OldAnnotation("Npgsql:Enum:token_type", "bearer,api_key")
                .OldAnnotation("Npgsql:Enum:user_role", "super_admin,admin,user");

            migrationBuilder.AddColumn<Guid>(
                name: "FlowKey",
                table: "FlowDefinitions",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<int>(
                name: "LifecycleStatus",
                table: "FlowDefinitions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "PublishedAt",
                table: "FlowDefinitions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "VersionNumber",
                table: "FlowDefinitions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql(
                """
                UPDATE "FlowDefinitions"
                SET "FlowKey" = "Id",
                    "VersionNumber" = 1,
                    "LifecycleStatus" = 1,
                    "PublishedAt" = "CreatedAt";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_FlowDefinitions_FlowKey_VersionNumber",
                table: "FlowDefinitions",
                columns: new[] { "FlowKey", "VersionNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FlowDefinitions_FlowKey_VersionNumber",
                table: "FlowDefinitions");

            migrationBuilder.DropColumn(
                name: "FlowKey",
                table: "FlowDefinitions");

            migrationBuilder.DropColumn(
                name: "LifecycleStatus",
                table: "FlowDefinitions");

            migrationBuilder.DropColumn(
                name: "PublishedAt",
                table: "FlowDefinitions");

            migrationBuilder.DropColumn(
                name: "VersionNumber",
                table: "FlowDefinitions");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:entry_type", "manual,reader,automatic,api_send,api_query")
                .Annotation("Npgsql:Enum:field_type", "text,number,date,document,email,select")
                .Annotation("Npgsql:Enum:instance_status", "in_progress,completed,cancelled")
                .Annotation("Npgsql:Enum:step_status", "pending,in_progress,completed,failed")
                .Annotation("Npgsql:Enum:step_type", "reader,user_task,external_monitor,automatic,api_send,api_query")
                .Annotation("Npgsql:Enum:token_type", "bearer,api_key")
                .Annotation("Npgsql:Enum:user_role", "super_admin,admin,user")
                .OldAnnotation("Npgsql:Enum:entry_type", "manual,reader,automatic,api_send,api_query")
                .OldAnnotation("Npgsql:Enum:field_type", "text,number,date,document,email,select")
                .OldAnnotation("Npgsql:Enum:flow_lifecycle_status", "draft,published,archived")
                .OldAnnotation("Npgsql:Enum:instance_status", "in_progress,completed,cancelled")
                .OldAnnotation("Npgsql:Enum:step_status", "pending,in_progress,completed,failed")
                .OldAnnotation("Npgsql:Enum:step_type", "reader,user_task,external_monitor,automatic,api_send,api_query")
                .OldAnnotation("Npgsql:Enum:token_type", "bearer,api_key")
                .OldAnnotation("Npgsql:Enum:user_role", "super_admin,admin,user");
        }
    }
}
