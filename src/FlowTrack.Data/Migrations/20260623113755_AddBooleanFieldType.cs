using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowTrack.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBooleanFieldType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:entry_type", "manual,reader,automatic,api_send,api_query")
                .Annotation("Npgsql:Enum:field_type", "text,number,date,document,email,select,boolean")
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
                .OldAnnotation("Npgsql:Enum:integration_trigger_type", "runtime,test")
                .OldAnnotation("Npgsql:Enum:step_status", "pending,in_progress,completed,failed")
                .OldAnnotation("Npgsql:Enum:step_type", "reader,user_task,external_monitor,automatic,api_send,api_query")
                .OldAnnotation("Npgsql:Enum:token_type", "bearer,api_key")
                .OldAnnotation("Npgsql:Enum:user_role", "super_admin,admin,user");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
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
                .OldAnnotation("Npgsql:Enum:field_type", "text,number,date,document,email,select,boolean")
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
