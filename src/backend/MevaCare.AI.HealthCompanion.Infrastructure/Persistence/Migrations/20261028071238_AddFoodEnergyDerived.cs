using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MevaCare.AI.HealthCompanion.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFoodEnergyDerived : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EnergyDerived",
                schema: "nutrition",
                table: "Foods",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EnergyDerived",
                schema: "nutrition",
                table: "Foods");
        }
    }
}
