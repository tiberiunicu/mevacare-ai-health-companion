using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MevaCare.AI.HealthCompanion.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFoodBrandBarcodeSearchText : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Barcode",
                schema: "nutrition",
                table: "Foods",
                type: "nvarchar(14)",
                maxLength: 14,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Brand",
                schema: "nutrition",
                table: "Foods",
                type: "nvarchar(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SearchText",
                schema: "nutrition",
                table: "Foods",
                type: "nvarchar(400)",
                maxLength: 400,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_Foods_Barcode",
                schema: "nutrition",
                table: "Foods",
                column: "Barcode");

            // Foods imported before this migration (USDA SR Legacy, plain ASCII descriptions) get the same value the application computes for new foods:
            // the lower-cased name. Names with diacritics only exist from the Open Food Facts import on, which writes the folded text itself.
            migrationBuilder.Sql("UPDATE [nutrition].[Foods] SET [SearchText] = LOWER(LEFT([Description], 400)) WHERE [SearchText] = N''");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Foods_Barcode",
                schema: "nutrition",
                table: "Foods");

            migrationBuilder.DropColumn(
                name: "Barcode",
                schema: "nutrition",
                table: "Foods");

            migrationBuilder.DropColumn(
                name: "Brand",
                schema: "nutrition",
                table: "Foods");

            migrationBuilder.DropColumn(
                name: "SearchText",
                schema: "nutrition",
                table: "Foods");
        }
    }
}
