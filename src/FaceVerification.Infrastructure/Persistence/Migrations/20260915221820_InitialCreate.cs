using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FaceVerification.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        private static readonly string[] StatusStartedColumns = ["Status", "StartedAt"];
        private static readonly string[] UserCreatedColumns = ["UserId", "CreatedAt"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "verification_sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ClientId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    NonceHash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LivenessTransactionId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    LivenessEvidenceHash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_verification_sessions", x => x.Id);
                    table.CheckConstraint("ck_verification_sessions_expiry", "\"ExpiresAt\" > \"CreatedAt\"");
                    table.CheckConstraint("ck_verification_sessions_status", "\"Status\" BETWEEN 0 AND 4");
                });

            migrationBuilder.CreateTable(
                name: "face_verification_results",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VerificationSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Decision = table.Column<int>(type: "integer", nullable: false),
                    SimilarityScore = table.Column<double>(type: "double precision", nullable: true),
                    AppliedMatchThreshold = table.Column<double>(type: "double precision", nullable: false),
                    AppliedNonMatchThreshold = table.Column<double>(type: "double precision", nullable: false),
                    ReferenceQualityScore = table.Column<double>(type: "double precision", nullable: false),
                    CapturedQualityScore = table.Column<double>(type: "double precision", nullable: false),
                    DetectedReferenceFaces = table.Column<int>(type: "integer", nullable: false),
                    DetectedCapturedFaces = table.Column<int>(type: "integer", nullable: false),
                    ModelName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ModelVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ProcessingTimeMs = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TraceId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_face_verification_results", x => x.Id);
                    table.CheckConstraint("ck_face_verification_results_similarity", "\"SimilarityScore\" IS NULL OR (\"SimilarityScore\" >= -1 AND \"SimilarityScore\" <= 1)");
                    table.CheckConstraint("ck_face_verification_results_thresholds", "\"AppliedNonMatchThreshold\" < \"AppliedMatchThreshold\"");
                    table.ForeignKey(
                        name: "FK_face_verification_results_verification_sessions_Verificatio~",
                        column: x => x.VerificationSessionId,
                        principalTable: "verification_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_face_verification_results_CreatedAt",
                table: "face_verification_results",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_face_verification_results_VerificationSessionId",
                table: "face_verification_results",
                column: "VerificationSessionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_verification_sessions_expires_at",
                table: "verification_sessions",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "ix_verification_sessions_status_started",
                table: "verification_sessions",
                columns: StatusStartedColumns);

            migrationBuilder.CreateIndex(
                name: "ix_verification_sessions_user_created",
                table: "verification_sessions",
                columns: UserCreatedColumns);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "face_verification_results");

            migrationBuilder.DropTable(
                name: "verification_sessions");
        }
    }
}
