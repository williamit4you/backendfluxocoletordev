using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowTrack.Data.Migrations
{
    /// <inheritdoc />
    public partial class IntegrationRuntimeAndHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:entry_type", "manual,reader,automatic,api_send,api_query")
                .Annotation("Npgsql:Enum:field_type", "text,number,date,document,email,select")
                .Annotation("Npgsql:Enum:flow_lifecycle_status", "draft,published,archived")
                .Annotation("Npgsql:Enum:instance_status", "in_progress,completed,cancelled")
                .Annotation("Npgsql:Enum:integration_trigger_type", "runtime,test")
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

            migrationBuilder.CreateTable(
                name: "IntegrationAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FlowInstanceId = table.Column<Guid>(type: "uuid", nullable: true),
                    FlowStepId = table.Column<Guid>(type: "uuid", nullable: false),
                    StepExecutionId = table.Column<Guid>(type: "uuid", nullable: true),
                    TriggerType = table.Column<int>(type: "integer", nullable: false),
                    Method = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    ResponseStatusCode = table.Column<int>(type: "integer", nullable: true),
                    Success = table.Column<bool>(type: "boolean", nullable: false),
                    DurationMs = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResponsePreview = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntegrationAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IntegrationAttempts_FlowInstances_FlowInstanceId",
                        column: x => x.FlowInstanceId,
                        principalTable: "FlowInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationAttempts_FlowInstanceId",
                table: "IntegrationAttempts",
                column: "FlowInstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationAttempts_FlowStepId",
                table: "IntegrationAttempts",
                column: "FlowStepId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IntegrationAttempts");

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
                .OldAnnotation("Npgsql:Enum:flow_lifecycle_status", "draft,published,archived")
                .OldAnnotation("Npgsql:Enum:instance_status", "in_progress,completed,cancelled")
                .OldAnnotation("Npgsql:Enum:integration_trigger_type", "runtime,test")
                .OldAnnotation("Npgsql:Enum:step_status", "pending,in_progress,completed,failed")
                .OldAnnotation("Npgsql:Enum:step_type", "reader,user_task,external_monitor,automatic,api_send,api_query")
                .OldAnnotation("Npgsql:Enum:token_type", "bearer,api_key")
                .OldAnnotation("Npgsql:Enum:user_role", "super_admin,admin,user");
        }
    }
}
