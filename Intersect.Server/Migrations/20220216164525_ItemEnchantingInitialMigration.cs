using Microsoft.EntityFrameworkCore.Migrations;

namespace Intersect.Server.Migrations
{
    public partial class ItemEnchantingInitialMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "Exp",
                table: "Player_Items",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "Level",
                table: "Player_Items",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<long>(
                name: "Exp",
                table: "Player_Bank",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "Level",
                table: "Player_Bank",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<long>(
                name: "Exp",
                table: "Guild_Bank",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "Level",
                table: "Guild_Bank",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<long>(
                name: "Exp",
                table: "Bag_Items",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "Level",
                table: "Bag_Items",
                nullable: false,
                defaultValue: 1);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Exp",
                table: "Player_Items");

            migrationBuilder.DropColumn(
                name: "Level",
                table: "Player_Items");

            migrationBuilder.DropColumn(
                name: "Exp",
                table: "Player_Bank");

            migrationBuilder.DropColumn(
                name: "Level",
                table: "Player_Bank");

            migrationBuilder.DropColumn(
                name: "Exp",
                table: "Guild_Bank");

            migrationBuilder.DropColumn(
                name: "Level",
                table: "Guild_Bank");

            migrationBuilder.DropColumn(
                name: "Exp",
                table: "Bag_Items");

            migrationBuilder.DropColumn(
                name: "Level",
                table: "Bag_Items");
        }
    }
}
