using MevaCare.AI.HealthCompanion.Modules.Assistant.Domain;
using MevaCare.AI.HealthCompanion.Modules.Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MevaCare.AI.HealthCompanion.Modules.Assistant.Persistence;

internal sealed class ConversationConfiguration : IEntityTypeConfiguration<Conversation>
{
    public void Configure(EntityTypeBuilder<Conversation> builder)
    {
        builder.ToTable("Conversations", "assistant");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Title).HasMaxLength(Conversation.MaxTitleLength).IsRequired();
        builder.HasIndex(c => new { c.UserId, c.LastMessageAtUtc });
        builder.HasOne<User>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(c => c.Messages).WithOne().HasForeignKey(m => m.ConversationId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(c => c.Messages).HasField("_messages").UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class ConversationMessageConfiguration : IEntityTypeConfiguration<ConversationMessage>
{
    public void Configure(EntityTypeBuilder<ConversationMessage> builder)
    {
        builder.ToTable("ChatMessages", "assistant");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Role).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(m => m.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(m => m.Content).IsRequired(); // nvarchar(max): assistant answers can be long
        builder.Property(m => m.Provider).HasMaxLength(32);
        builder.Property(m => m.Model).HasMaxLength(100);
        builder.HasIndex(m => new { m.ConversationId, m.Sequence }).IsUnique();
        builder.HasIndex(m => new { m.ConversationId, m.ClientMessageId })
            .IsUnique()
            .HasFilter("[ClientMessageId] IS NOT NULL");
    }
}
