using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowTrack.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMinioStorageConfiguration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MinioConfigurationEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Endpoint = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    AccessKey = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    SecretKey = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    PublicUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MinioConfigurationEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StoredFileEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FlowInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    StepExecutionId = table.Column<Guid>(type: "uuid", nullable: false),
                    MinioBucketId = table.Column<Guid>(type: "uuid", nullable: false),
                    FieldKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    BucketName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    ObjectKey = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    FileName = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    IsPhoto = table.Column<bool>(type: "boolean", nullable: false),
                    UploadedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    UploadedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoredFileEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StoredFileEntries_FlowInstances_FlowInstanceId",
                        column: x => x.FlowInstanceId,
                        principalTable: "FlowInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MinioBucketEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MinioConfigurationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    BucketName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MinioBucketEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MinioBucketEntries_MinioConfigurationEntries_MinioConfigura~",
                        column: x => x.MinioConfigurationId,
                        principalTable: "MinioConfigurationEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MinioBucketEntries_MinioConfigurationId_BucketName",
                table: "MinioBucketEntries",
                columns: new[] { "MinioConfigurationId", "BucketName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StoredFileEntries_FlowInstanceId",
                table: "StoredFileEntries",
                column: "FlowInstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_StoredFileEntries_StepExecutionId",
                table: "StoredFileEntries",
                column: "StepExecutionId");

            migrationBuilder.CreateIndex(
                name: "IX_StoredFileEntries_StepExecutionId_FieldKey",
                table: "StoredFileEntries",
                columns: new[] { "StepExecutionId", "FieldKey" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MinioBucketEntries");

            migrationBuilder.DropTable(
                name: "StoredFileEntries");

            migrationBuilder.DropTable(
                name: "MinioConfigurationEntries");
        }
    }
}
