using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CashGear.Backup.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Destinations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Location = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false, collation: "NOCASE"),
                    ProtectedPassword = table.Column<byte[]>(type: "BLOB", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Destinations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Trigger = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    FinishedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    SnapshotId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Files = table.Column<long>(type: "INTEGER", nullable: false),
                    Bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    FailedItemCount = table.Column<long>(type: "INTEGER", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: true),
                    RestoreTarget = table.Column<string>(type: "TEXT", nullable: true),
                    FailedItems = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Runs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "Jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    DestinationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourcePath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false, collation: "NOCASE"),
                    KeepLatest = table.Column<int>(type: "INTEGER", nullable: false),
                    KeepHourly = table.Column<int>(type: "INTEGER", nullable: false),
                    KeepDaily = table.Column<int>(type: "INTEGER", nullable: false),
                    KeepWeekly = table.Column<int>(type: "INTEGER", nullable: false),
                    KeepMonthly = table.Column<int>(type: "INTEGER", nullable: false),
                    KeepAnnual = table.Column<int>(type: "INTEGER", nullable: false),
                    Excludes = table.Column<string>(type: "TEXT", nullable: false),
                    Compression = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ScheduleKind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ScheduleMinuteOfDay = table.Column<int>(type: "INTEGER", nullable: false),
                    ScheduleDays = table.Column<int>(type: "INTEGER", nullable: false),
                    ScheduleIntervalHours = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Jobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Jobs_Destinations_DestinationId",
                        column: x => x.DestinationId,
                        principalTable: "Destinations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Destinations_Location",
                table: "Destinations",
                column: "Location",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_DestinationId_SourcePath",
                table: "Jobs",
                columns: new[] { "DestinationId", "SourcePath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Runs_JobId_StartedAt",
                table: "Runs",
                columns: new[] { "JobId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Runs_State",
                table: "Runs",
                column: "State");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Jobs");

            migrationBuilder.DropTable(
                name: "Runs");

            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropTable(
                name: "Destinations");
        }
    }
}
