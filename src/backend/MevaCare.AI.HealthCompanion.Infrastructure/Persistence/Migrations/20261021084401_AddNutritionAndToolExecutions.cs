using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MevaCare.AI.HealthCompanion.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNutritionAndToolExecutions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "nutrition");

            migrationBuilder.CreateTable(
                name: "Foods",
                schema: "nutrition",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    SourceFoodId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Category = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    Preparation = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Foods", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Meals",
                schema: "nutrition",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    EatenAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LocalDate = table.Column<DateOnly>(type: "date", nullable: false),
                    UtcOffsetMinutes = table.Column<int>(type: "int", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Origin = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SourceProposalId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Meals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Meals_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "identity",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NutritionTargets",
                schema: "nutrition",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Calories = table.Column<decimal>(type: "decimal(10,1)", precision: 10, scale: 1, nullable: true),
                    ProteinG = table.Column<decimal>(type: "decimal(10,1)", precision: 10, scale: 1, nullable: true),
                    CarbohydratesG = table.Column<decimal>(type: "decimal(10,1)", precision: 10, scale: 1, nullable: true),
                    FatG = table.Column<decimal>(type: "decimal(10,1)", precision: 10, scale: 1, nullable: true),
                    FiberG = table.Column<decimal>(type: "decimal(10,1)", precision: 10, scale: 1, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NutritionTargets", x => x.Id);
                    table.CheckConstraint("CK_NutritionTargets_Values", "([Calories] IS NULL OR [Calories] BETWEEN 0 AND 20000) AND ([ProteinG] IS NULL OR [ProteinG] BETWEEN 0 AND 1000) AND ([CarbohydratesG] IS NULL OR [CarbohydratesG] BETWEEN 0 AND 2000) AND ([FatG] IS NULL OR [FatG] BETWEEN 0 AND 1000) AND ([FiberG] IS NULL OR [FiberG] BETWEEN 0 AND 300)");
                    table.ForeignKey(
                        name: "FK_NutritionTargets_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "identity",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ToolExecutions",
                schema: "assistant",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssistantMessageSequence = table.Column<int>(type: "int", nullable: false),
                    ToolName = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    ArgumentsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ResultJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ErrorCode = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    DurationMs = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CommittedMealId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ToolExecutions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ToolExecutions_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalSchema: "assistant",
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FoodNutrients",
                schema: "nutrition",
                columns: table => new
                {
                    FoodId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    AmountPer100g = table.Column<decimal>(type: "decimal(12,4)", precision: 12, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FoodNutrients", x => new { x.FoodId, x.Type });
                    table.CheckConstraint("CK_FoodNutrients_Amount", "[AmountPer100g] >= 0");
                    table.ForeignKey(
                        name: "FK_FoodNutrients_Foods_FoodId",
                        column: x => x.FoodId,
                        principalSchema: "nutrition",
                        principalTable: "Foods",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FoodPortions",
                schema: "nutrition",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FoodId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(12,4)", precision: 12, scale: 4, nullable: false),
                    Unit = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    GramWeight = table.Column<decimal>(type: "decimal(12,4)", precision: 12, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FoodPortions", x => x.Id);
                    table.CheckConstraint("CK_FoodPortions_Weight", "[GramWeight] > 0 AND [Amount] > 0");
                    table.ForeignKey(
                        name: "FK_FoodPortions_Foods_FoodId",
                        column: x => x.FoodId,
                        principalSchema: "nutrition",
                        principalTable: "Foods",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MealItems",
                schema: "nutrition",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MealId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FoodId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    QuantityGrams = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    Preparation = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IsEstimated = table.Column<bool>(type: "bit", nullable: false),
                    SourceLabel = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Calories = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: true),
                    ProteinG = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: true),
                    CarbohydratesG = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: true),
                    FatG = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: true),
                    FiberG = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MealItems", x => x.Id);
                    table.CheckConstraint("CK_MealItems_Nutrients", "([Calories] IS NULL OR [Calories] >= 0) AND ([ProteinG] IS NULL OR [ProteinG] >= 0) AND ([CarbohydratesG] IS NULL OR [CarbohydratesG] >= 0) AND ([FatG] IS NULL OR [FatG] >= 0) AND ([FiberG] IS NULL OR [FiberG] >= 0)");
                    table.CheckConstraint("CK_MealItems_Quantity", "[QuantityGrams] > 0 AND [QuantityGrams] <= 10000");
                    table.ForeignKey(
                        name: "FK_MealItems_Meals_MealId",
                        column: x => x.MealId,
                        principalSchema: "nutrition",
                        principalTable: "Meals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FoodPortions_FoodId",
                schema: "nutrition",
                table: "FoodPortions",
                column: "FoodId");

            migrationBuilder.CreateIndex(
                name: "IX_Foods_Source_SourceFoodId",
                schema: "nutrition",
                table: "Foods",
                columns: new[] { "Source", "SourceFoodId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MealItems_MealId",
                schema: "nutrition",
                table: "MealItems",
                column: "MealId");

            migrationBuilder.CreateIndex(
                name: "IX_Meals_SourceProposalId",
                schema: "nutrition",
                table: "Meals",
                column: "SourceProposalId",
                unique: true,
                filter: "[SourceProposalId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Meals_UserId_LocalDate",
                schema: "nutrition",
                table: "Meals",
                columns: new[] { "UserId", "LocalDate" });

            migrationBuilder.CreateIndex(
                name: "IX_NutritionTargets_UserId",
                schema: "nutrition",
                table: "NutritionTargets",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ToolExecutions_ConversationId_AssistantMessageSequence",
                schema: "assistant",
                table: "ToolExecutions",
                columns: new[] { "ConversationId", "AssistantMessageSequence" });

            migrationBuilder.CreateIndex(
                name: "IX_ToolExecutions_UserId_ConversationId",
                schema: "assistant",
                table: "ToolExecutions",
                columns: new[] { "UserId", "ConversationId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FoodNutrients",
                schema: "nutrition");

            migrationBuilder.DropTable(
                name: "FoodPortions",
                schema: "nutrition");

            migrationBuilder.DropTable(
                name: "MealItems",
                schema: "nutrition");

            migrationBuilder.DropTable(
                name: "NutritionTargets",
                schema: "nutrition");

            migrationBuilder.DropTable(
                name: "ToolExecutions",
                schema: "assistant");

            migrationBuilder.DropTable(
                name: "Foods",
                schema: "nutrition");

            migrationBuilder.DropTable(
                name: "Meals",
                schema: "nutrition");
        }
    }
}
