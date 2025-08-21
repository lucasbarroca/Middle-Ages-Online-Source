using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Intersect.Server.Migrations.MySql.Game
{
    /// <inheritdoc />
    public partial class PlaceableItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Placeable",
                table: "Items",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PlacedSprite",
                table: "Items",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "PlacedAnimation",
                table: "Items",
                nullable: false,
                defaultValue: Guid.Empty);

            migrationBuilder.AddColumn<string>(
                name: "PlacedItems",
                table: "Maps",
                nullable: false,
                defaultValue: "[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Placeable",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "PlacedSprite",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "PlacedAnimation",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "PlacedItems",
                table: "Maps");
        }
    }
}

