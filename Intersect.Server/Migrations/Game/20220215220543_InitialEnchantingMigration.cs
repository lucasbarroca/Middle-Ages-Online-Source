using Microsoft.EntityFrameworkCore.Migrations;

namespace Intersect.Server.Migrations.Game
{
    public partial class InitialEnchantingMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "BaseExp",
                table: "Items",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "BonusIncrease",
                table: "Items",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "ExpIncrease",
                table: "Items",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<bool>(
                name: "IncreasePercentage",
                table: "Items",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "MaxLevel",
                table: "Items",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SlotsRequired",
                table: "Items",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "StatIncreases",
                table: "Items",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VitalIncreases",
                table: "Items",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BaseExp",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "BonusIncrease",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "ExpIncrease",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "IncreasePercentage",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "MaxLevel",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "SlotsRequired",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "StatIncreases",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "VitalIncreases",
                table: "Items");
        }
    }
}
