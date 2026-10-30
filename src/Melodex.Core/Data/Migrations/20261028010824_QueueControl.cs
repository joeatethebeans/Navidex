using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Melodex.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class QueueControl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Paused",
                table: "Requests",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<double>(
                name: "QueueOrder",
                table: "Requests",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<bool>(
                name: "Paused",
                table: "BackgroundTasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<double>(
                name: "QueueOrder",
                table: "BackgroundTasks",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Paused",
                table: "Requests");

            migrationBuilder.DropColumn(
                name: "QueueOrder",
                table: "Requests");

            migrationBuilder.DropColumn(
                name: "Paused",
                table: "BackgroundTasks");

            migrationBuilder.DropColumn(
                name: "QueueOrder",
                table: "BackgroundTasks");
        }
    }
}
