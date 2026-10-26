using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Navidex.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class ListenNow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "EditionId",
                table: "ListenNowItems",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EditionId",
                table: "ListenNowItems");
        }
    }
}
