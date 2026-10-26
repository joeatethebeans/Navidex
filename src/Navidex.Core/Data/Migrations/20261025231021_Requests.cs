using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Navidex.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class Requests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ChoiceJson",
                table: "Requests",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Detail",
                table: "Requests",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "EditionId",
                table: "Requests",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Title",
                table: "Requests",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ManifestJson",
                table: "PluginInstances",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CandidatesJson",
                table: "Jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StagingDirectory",
                table: "Jobs",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ChoiceJson",
                table: "Requests");

            migrationBuilder.DropColumn(
                name: "Detail",
                table: "Requests");

            migrationBuilder.DropColumn(
                name: "EditionId",
                table: "Requests");

            migrationBuilder.DropColumn(
                name: "Title",
                table: "Requests");

            migrationBuilder.DropColumn(
                name: "ManifestJson",
                table: "PluginInstances");

            migrationBuilder.DropColumn(
                name: "CandidatesJson",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "StagingDirectory",
                table: "Jobs");
        }
    }
}
