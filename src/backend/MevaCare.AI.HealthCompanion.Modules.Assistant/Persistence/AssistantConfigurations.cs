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

internal sealed class ToolExecutionConfiguration : IEntityTypeConfiguration<ToolExecution>
{
    public void Configure(EntityTypeBuilder<ToolExecution> builder)
    {
        builder.ToTable("ToolExecutions", "assistant");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.ToolName).HasMaxLength(60).IsRequired();
        builder.Property(t => t.Kind).HasConversion<string>().HasMaxLength(10).IsRequired();
        builder.Property(t => t.Outcome).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(t => t.ArgumentsJson).IsRequired();
        builder.Property(t => t.ErrorCode).HasMaxLength(60);
        builder.HasIndex(t => new { t.UserId, t.ConversationId });
        builder.HasIndex(t => new { t.ConversationId, t.AssistantMessageSequence });
        // Deleting a conversation (or, through it, the user) deletes its tool audit rows.
        builder.HasOne<Conversation>().WithMany().HasForeignKey(t => t.ConversationId).OnDelete(DeleteBehavior.Cascade);
    }
}
