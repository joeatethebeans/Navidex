using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Melodex.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class PlaylistPinsAndCovers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CoverChangedAt",
                table: "Playlists",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CoverFile",
                table: "Playlists",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CoverHash",
                table: "Playlists",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CoverNeedsPush",
                table: "Playlists",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "CoverSyncedAt",
                table: "Playlists",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CoverType",
                table: "Playlists",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PinnedAt",
                table: "Playlists",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ServerCoverHash",
                table: "Playlists",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CoverChangedAt",
                table: "Playlists");

            migrationBuilder.DropColumn(
                name: "CoverFile",
                table: "Playlists");

            migrationBuilder.DropColumn(
                name: "CoverHash",
                table: "Playlists");

            migrationBuilder.DropColumn(
                name: "CoverNeedsPush",
                table: "Playlists");

            migrationBuilder.DropColumn(
                name: "CoverSyncedAt",
                table: "Playlists");

            migrationBuilder.DropColumn(
                name: "CoverType",
                table: "Playlists");

            migrationBuilder.DropColumn(
                name: "PinnedAt",
                table: "Playlists");

            migrationBuilder.DropColumn(
                name: "ServerCoverHash",
                table: "Playlists");
        }
    }
}
