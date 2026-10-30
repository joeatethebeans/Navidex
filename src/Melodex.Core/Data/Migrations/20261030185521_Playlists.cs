using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Melodex.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class Playlists : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MediaServerPassword",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MediaServerUsername",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlaylistSyncError",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PlaylistsSyncedAt",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Playlists",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Comment = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ServerInstanceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ServerId = table.Column<string>(type: "TEXT", nullable: true),
                    ServerChangedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SyncedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NeedsPush = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Playlists", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Playlists_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PlaylistEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PlaylistId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    TrackId = table.Column<Guid>(type: "TEXT", nullable: true),
                    FileId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Artist = table.Column<string>(type: "TEXT", nullable: false),
                    Album = table.Column<string>(type: "TEXT", nullable: true),
                    DurationSeconds = table.Column<double>(type: "REAL", nullable: true),
                    ServerTrackId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlaylistEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlaylistEntries_Playlists_PlaylistId",
                        column: x => x.PlaylistId,
                        principalTable: "Playlists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlaylistEntries_FileId",
                table: "PlaylistEntries",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_PlaylistEntries_PlaylistId_Position",
                table: "PlaylistEntries",
                columns: new[] { "PlaylistId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_PlaylistEntries_TrackId",
                table: "PlaylistEntries",
                column: "TrackId");

            migrationBuilder.CreateIndex(
                name: "IX_Playlists_UserId",
                table: "Playlists",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlaylistEntries");

            migrationBuilder.DropTable(
                name: "Playlists");

            migrationBuilder.DropColumn(
                name: "MediaServerPassword",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "MediaServerUsername",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PlaylistSyncError",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PlaylistsSyncedAt",
                table: "Users");
        }
    }
}
