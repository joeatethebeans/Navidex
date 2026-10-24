using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Navidex.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class LibraryAndMatching : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Crosswalk_EntityKind_Provider_Value",
                table: "Crosswalk");

            migrationBuilder.AddColumn<Guid>(
                name: "ArtistId",
                table: "LibraryFiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "MatchedAt",
                table: "LibraryFiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewReason",
                table: "LibraryFiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SuggestedEditionId",
                table: "LibraryFiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SuggestedTrackId",
                table: "LibraryFiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Formats",
                table: "Editions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TrackCount",
                table: "Editions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "AlbumsRefreshedAt",
                table: "Artists",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "InLibrary",
                table: "Artists",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "EditionsRefreshedAt",
                table: "Albums",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HasOfficialRelease",
                table: "Albums",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Ignored",
                table: "Albums",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "BackgroundTasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Progress = table.Column<string>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackgroundTasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    ValueJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Key);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LibraryFiles_ArtistId",
                table: "LibraryFiles",
                column: "ArtistId");

            migrationBuilder.CreateIndex(
                name: "IX_LibraryFiles_MatchedAt",
                table: "LibraryFiles",
                column: "MatchedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Crosswalk_Provider_Value",
                table: "Crosswalk",
                columns: new[] { "Provider", "Value" });

            migrationBuilder.CreateIndex(
                name: "IX_Crosswalk_ProviderId",
                table: "Crosswalk",
                columns: new[] { "EntityKind", "Provider", "Value" },
                unique: true,
                filter: "\"Provider\" NOT IN ('isrc', 'upc')");

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundTasks_Status_CreatedAt",
                table: "BackgroundTasks",
                columns: new[] { "Status", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BackgroundTasks");

            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropIndex(
                name: "IX_LibraryFiles_ArtistId",
                table: "LibraryFiles");

            migrationBuilder.DropIndex(
                name: "IX_LibraryFiles_MatchedAt",
                table: "LibraryFiles");

            migrationBuilder.DropIndex(
                name: "IX_Crosswalk_Provider_Value",
                table: "Crosswalk");

            migrationBuilder.DropIndex(
                name: "IX_Crosswalk_ProviderId",
                table: "Crosswalk");

            migrationBuilder.DropColumn(
                name: "ArtistId",
                table: "LibraryFiles");

            migrationBuilder.DropColumn(
                name: "MatchedAt",
                table: "LibraryFiles");

            migrationBuilder.DropColumn(
                name: "ReviewReason",
                table: "LibraryFiles");

            migrationBuilder.DropColumn(
                name: "SuggestedEditionId",
                table: "LibraryFiles");

            migrationBuilder.DropColumn(
                name: "SuggestedTrackId",
                table: "LibraryFiles");

            migrationBuilder.DropColumn(
                name: "Formats",
                table: "Editions");

            migrationBuilder.DropColumn(
                name: "TrackCount",
                table: "Editions");

            migrationBuilder.DropColumn(
                name: "AlbumsRefreshedAt",
                table: "Artists");

            migrationBuilder.DropColumn(
                name: "InLibrary",
                table: "Artists");

            migrationBuilder.DropColumn(
                name: "EditionsRefreshedAt",
                table: "Albums");

            migrationBuilder.DropColumn(
                name: "HasOfficialRelease",
                table: "Albums");

            migrationBuilder.DropColumn(
                name: "Ignored",
                table: "Albums");

            migrationBuilder.CreateIndex(
                name: "IX_Crosswalk_EntityKind_Provider_Value",
                table: "Crosswalk",
                columns: new[] { "EntityKind", "Provider", "Value" },
                unique: true);
        }
    }
}
