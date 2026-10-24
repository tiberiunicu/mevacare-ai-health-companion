using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MevaCare.AI.HealthCompanion.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMeasurementsAndIntegrations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "integrations");

            migrationBuilder.EnsureSchema(
                name: "measurements");

            migrationBuilder.CreateTable(
                name: "ConnectedAccounts",
                schema: "integrations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ExternalUserId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Scopes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    AccessTokenProtected = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    RefreshTokenProtected = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    AccessTokenExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ConnectedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSyncAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastAttemptAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastSyncStatus = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectedAccounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConnectedAccounts_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "identity",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MeasurementSources",
                schema: "measurements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MeasurementSources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MeasurementSources_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "identity",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OAuthStates",
                schema: "integrations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    StateHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OAuthStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OAuthStates_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "identity",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SyncJobs",
                schema: "integrations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConnectedAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Trigger = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FinishedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RangeFromUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RangeToUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RecordsFetched = table.Column<int>(type: "int", nullable: false),
                    RecordsAdded = table.Column<int>(type: "int", nullable: false),
                    RecordsUpdated = table.Column<int>(type: "int", nullable: false),
                    ErrorCode = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncJobs_ConnectedAccounts_ConnectedAccountId",
                        column: x => x.ConnectedAccountId,
                        principalSchema: "integrations",
                        principalTable: "ConnectedAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "HealthMeasurements",
                schema: "measurements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Metric = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Value = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    Unit = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    MeasuredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UtcOffsetMinutes = table.Column<int>(type: "int", nullable: false),
                    LocalDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ExternalId = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HealthMeasurements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HealthMeasurements_MeasurementSources_SourceId",
                        column: x => x.SourceId,
                        principalSchema: "measurements",
                        principalTable: "MeasurementSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SleepSessions",
                schema: "measurements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExternalId = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    StartUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EndUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UtcOffsetMinutes = table.Column<int>(type: "int", nullable: false),
                    LocalDate = table.Column<DateOnly>(type: "date", nullable: false),
                    IsNap = table.Column<bool>(type: "bit", nullable: false),
                    IsScored = table.Column<bool>(type: "bit", nullable: false),
                    TimeInBedMinutes = table.Column<int>(type: "int", nullable: true),
                    AwakeMinutes = table.Column<int>(type: "int", nullable: true),
                    LightMinutes = table.Column<int>(type: "int", nullable: true),
                    DeepMinutes = table.Column<int>(type: "int", nullable: true),
                    RemMinutes = table.Column<int>(type: "int", nullable: true),
                    PerformancePercent = table.Column<decimal>(type: "decimal(7,3)", precision: 7, scale: 3, nullable: true),
                    EfficiencyPercent = table.Column<decimal>(type: "decimal(7,3)", precision: 7, scale: 3, nullable: true),
                    ConsistencyPercent = table.Column<decimal>(type: "decimal(7,3)", precision: 7, scale: 3, nullable: true),
                    RespiratoryRate = table.Column<decimal>(type: "decimal(7,3)", precision: 7, scale: 3, nullable: true),
                    DisturbanceCount = table.Column<int>(type: "int", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SleepSessions", x => x.Id);
                    table.CheckConstraint("CK_SleepSessions_Times", "[EndUtc] >= [StartUtc]");
                    table.ForeignKey(
                        name: "FK_SleepSessions_MeasurementSources_SourceId",
                        column: x => x.SourceId,
                        principalSchema: "measurements",
                        principalTable: "MeasurementSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Workouts",
                schema: "measurements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExternalId = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    SportName = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    StartUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EndUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UtcOffsetMinutes = table.Column<int>(type: "int", nullable: false),
                    LocalDate = table.Column<DateOnly>(type: "date", nullable: false),
                    IsScored = table.Column<bool>(type: "bit", nullable: false),
                    Strain = table.Column<decimal>(type: "decimal(7,3)", precision: 7, scale: 3, nullable: true),
                    AverageHeartRate = table.Column<int>(type: "int", nullable: true),
                    MaxHeartRate = table.Column<int>(type: "int", nullable: true),
                    Calories = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: true),
                    DistanceMeters = table.Column<decimal>(type: "decimal(12,2)", precision: 12, scale: 2, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Workouts", x => x.Id);
                    table.CheckConstraint("CK_Workouts_Times", "[EndUtc] >= [StartUtc]");
                    table.ForeignKey(
                        name: "FK_Workouts_MeasurementSources_SourceId",
                        column: x => x.SourceId,
                        principalSchema: "measurements",
                        principalTable: "MeasurementSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedAccounts_Provider_ExternalUserId",
                schema: "integrations",
                table: "ConnectedAccounts",
                columns: new[] { "Provider", "ExternalUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedAccounts_UserId_Provider",
                schema: "integrations",
                table: "ConnectedAccounts",
                columns: new[] { "UserId", "Provider" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HealthMeasurements_SourceId_Metric_ExternalId",
                schema: "measurements",
                table: "HealthMeasurements",
                columns: new[] { "SourceId", "Metric", "ExternalId" },
                unique: true,
                filter: "[ExternalId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_HealthMeasurements_UserId_Metric_LocalDate",
                schema: "measurements",
                table: "HealthMeasurements",
                columns: new[] { "UserId", "Metric", "LocalDate" });

            migrationBuilder.CreateIndex(
                name: "IX_MeasurementSources_UserId_Provider",
                schema: "measurements",
                table: "MeasurementSources",
                columns: new[] { "UserId", "Provider" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OAuthStates_ExpiresAtUtc",
                schema: "integrations",
                table: "OAuthStates",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_OAuthStates_StateHash",
                schema: "integrations",
                table: "OAuthStates",
                column: "StateHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OAuthStates_UserId",
                schema: "integrations",
                table: "OAuthStates",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_SleepSessions_SourceId_ExternalId",
                schema: "measurements",
                table: "SleepSessions",
                columns: new[] { "SourceId", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SleepSessions_UserId_LocalDate",
                schema: "measurements",
                table: "SleepSessions",
                columns: new[] { "UserId", "LocalDate" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncJobs_ConnectedAccountId_StartedAtUtc",
                schema: "integrations",
                table: "SyncJobs",
                columns: new[] { "ConnectedAccountId", "StartedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Workouts_SourceId_ExternalId",
                schema: "measurements",
                table: "Workouts",
                columns: new[] { "SourceId", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Workouts_UserId_LocalDate",
                schema: "measurements",
                table: "Workouts",
                columns: new[] { "UserId", "LocalDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HealthMeasurements",
                schema: "measurements");

            migrationBuilder.DropTable(
                name: "OAuthStates",
                schema: "integrations");

            migrationBuilder.DropTable(
                name: "SleepSessions",
                schema: "measurements");

            migrationBuilder.DropTable(
                name: "SyncJobs",
                schema: "integrations");

            migrationBuilder.DropTable(
                name: "Workouts",
                schema: "measurements");

            migrationBuilder.DropTable(
                name: "ConnectedAccounts",
                schema: "integrations");

            migrationBuilder.DropTable(
                name: "MeasurementSources",
                schema: "measurements");
        }
    }
}
