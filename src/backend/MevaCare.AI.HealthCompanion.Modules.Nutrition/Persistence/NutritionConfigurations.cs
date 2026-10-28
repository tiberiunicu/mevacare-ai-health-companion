using MevaCare.AI.HealthCompanion.Modules.Identity.Domain;
using MevaCare.AI.HealthCompanion.Modules.Nutrition.Domain;
using MevaCare.AI.HealthCompanion.Modules.Nutrition.FoodSearch;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MevaCare.AI.HealthCompanion.Modules.Nutrition.Persistence;

internal sealed class FoodConfiguration : IEntityTypeConfiguration<Food>
{
    public void Configure(EntityTypeBuilder<Food> builder)
    {
        builder.ToTable("Foods", "nutrition");
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Source).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(f => f.SourceFoodId).HasMaxLength(40).IsRequired();
        builder.Property(f => f.Description).HasMaxLength(300).IsRequired();
        builder.Property(f => f.Category).HasMaxLength(120);
        builder.Property(f => f.Preparation).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(f => f.Brand).HasMaxLength(120);
        builder.Property(f => f.Barcode).HasMaxLength(14);
        builder.Property(f => f.SearchText).HasMaxLength(FoodText.MaxSearchTextLength).IsRequired();
        builder.Ignore(f => f.SourceLabel);
        builder.Ignore(f => f.IsBranded);
        builder.HasIndex(f => f.Barcode);
        builder.HasIndex(f => new { f.Source, f.SourceFoodId }).IsUnique();
        builder.HasMany(f => f.Nutrients).WithOne().HasForeignKey(n => n.FoodId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(f => f.Portions).WithOne().HasForeignKey(p => p.FoodId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(f => f.Nutrients).HasField("_nutrients").UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(f => f.Portions).HasField("_portions").UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class FoodNutrientConfiguration : IEntityTypeConfiguration<FoodNutrient>
{
    public void Configure(EntityTypeBuilder<FoodNutrient> builder)
    {
        builder.ToTable("FoodNutrients", "nutrition", t => t.HasCheckConstraint("CK_FoodNutrients_Amount", "[AmountPer100g] >= 0"));
        builder.HasKey(n => new { n.FoodId, n.Type });
        builder.Property(n => n.Type).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(n => n.AmountPer100g).HasPrecision(12, 4);
    }
}

internal sealed class FoodPortionConfiguration : IEntityTypeConfiguration<FoodPortion>
{
    public void Configure(EntityTypeBuilder<FoodPortion> builder)
    {
        builder.ToTable("FoodPortions", "nutrition", t => t.HasCheckConstraint("CK_FoodPortions_Weight", "[GramWeight] > 0 AND [Amount] > 0"));
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Unit).HasMaxLength(60).IsRequired();
        builder.Property(p => p.Description).HasMaxLength(200);
        builder.Property(p => p.Amount).HasPrecision(12, 4);
        builder.Property(p => p.GramWeight).HasPrecision(12, 4);
        builder.HasIndex(p => p.FoodId);
    }
}

internal sealed class MealConfiguration : IEntityTypeConfiguration<Meal>
{
    public void Configure(EntityTypeBuilder<Meal> builder)
    {
        builder.ToTable("Meals", "nutrition");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Type).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(m => m.Origin).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(m => m.Notes).HasMaxLength(500);
        builder.HasIndex(m => new { m.UserId, m.LocalDate });
        builder.HasIndex(m => m.SourceProposalId).IsUnique().HasFilter("[SourceProposalId] IS NOT NULL");
        builder.HasOne<User>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(m => m.Items).WithOne().HasForeignKey(i => i.MealId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(m => m.Items).HasField("_items").UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class MealItemConfiguration : IEntityTypeConfiguration<MealItem>
{
    public void Configure(EntityTypeBuilder<MealItem> builder)
    {
        builder.ToTable("MealItems", "nutrition", t =>
        {
            t.HasCheckConstraint("CK_MealItems_Quantity", "[QuantityGrams] > 0 AND [QuantityGrams] <= 10000");
            t.HasCheckConstraint(
                "CK_MealItems_Nutrients",
                "([Calories] IS NULL OR [Calories] >= 0) AND ([ProteinG] IS NULL OR [ProteinG] >= 0) AND ([CarbohydratesG] IS NULL OR [CarbohydratesG] >= 0) AND ([FatG] IS NULL OR [FatG] >= 0) AND ([FiberG] IS NULL OR [FiberG] >= 0)");
        });
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Name).HasMaxLength(300).IsRequired();
        builder.Property(i => i.Preparation).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(i => i.SourceLabel).HasMaxLength(100);
        builder.Property(i => i.QuantityGrams).HasPrecision(10, 2);
        builder.Property(i => i.Calories).HasPrecision(10, 2);
        builder.Property(i => i.ProteinG).HasPrecision(10, 2);
        builder.Property(i => i.CarbohydratesG).HasPrecision(10, 2);
        builder.Property(i => i.FatG).HasPrecision(10, 2);
        builder.Property(i => i.FiberG).HasPrecision(10, 2);
        builder.Ignore(i => i.Nutrients);
        builder.HasIndex(i => i.MealId);
    }
}

internal sealed class NutritionTargetConfiguration : IEntityTypeConfiguration<NutritionTarget>
{
    public void Configure(EntityTypeBuilder<NutritionTarget> builder)
    {
        builder.ToTable("NutritionTargets", "nutrition", t =>
            t.HasCheckConstraint(
                "CK_NutritionTargets_Values",
                "([Calories] IS NULL OR [Calories] BETWEEN 0 AND 20000) AND ([ProteinG] IS NULL OR [ProteinG] BETWEEN 0 AND 1000) AND ([CarbohydratesG] IS NULL OR [CarbohydratesG] BETWEEN 0 AND 2000) AND ([FatG] IS NULL OR [FatG] BETWEEN 0 AND 1000) AND ([FiberG] IS NULL OR [FiberG] BETWEEN 0 AND 300)"));
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Calories).HasPrecision(10, 1);
        builder.Property(t => t.ProteinG).HasPrecision(10, 1);
        builder.Property(t => t.CarbohydratesG).HasPrecision(10, 1);
        builder.Property(t => t.FatG).HasPrecision(10, 1);
        builder.Property(t => t.FiberG).HasPrecision(10, 1);
        builder.HasIndex(t => t.UserId).IsUnique();
        builder.HasOne<User>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
