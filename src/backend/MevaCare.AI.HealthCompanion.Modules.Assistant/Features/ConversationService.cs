using MevaCare.AI.HealthCompanion.Contracts.Abstractions;
using MevaCare.AI.HealthCompanion.Contracts.Errors;
using MevaCare.AI.HealthCompanion.Infrastructure.Persistence;
using MevaCare.AI.HealthCompanion.Modules.Assistant.Domain;
using Microsoft.EntityFrameworkCore;

namespace MevaCare.AI.HealthCompanion.Modules.Assistant.Features;

/// <summary>
/// Reads and deletes conversations. Browsing the history does not call the LLM, so it needs no AI consent,
/// and the user can always see and delete what is stored about them.
/// </summary>
public sealed class ConversationService(AppDbContext db, IAuditLog audit)
{
    private const int MaxConversations = 100;

    public async Task<IReadOnlyList<ConversationSummaryDto>> ListAsync(Guid userId, CancellationToken ct) =>
        await db.Set<Conversation>().AsNoTracking()
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.LastMessageAtUtc)
            .Take(MaxConversations)
            .Select(c => new ConversationSummaryDto(c.Id, c.Title, c.LastMessageAtUtc))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<ChatMessageDto>> GetMessagesAsync(Guid userId, Guid conversationId, CancellationToken ct)
    {
        if (!await db.Set<Conversation>().AnyAsync(c => c.Id == conversationId && c.UserId == userId, ct))
        {
            throw new NotFoundException("Conversation not found.");
        }

        var messages = await db.Set<ConversationMessage>().AsNoTracking()
            .Where(m => m.ConversationId == conversationId)
            .OrderBy(m => m.Sequence)
            .ToListAsync(ct);
        return messages.Select(m => m.ToDto()).ToList();
    }

    public async Task DeleteAsync(Guid userId, Guid conversationId, CancellationToken ct)
    {
        var conversation = await db.Set<Conversation>().SingleOrDefaultAsync(c => c.Id == conversationId && c.UserId == userId, ct)
            ?? throw new NotFoundException("Conversation not found.");
        db.Remove(conversation); // messages are removed by the cascading foreign key
        audit.Record("assistant.conversation_deleted", userId);
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Adds the chat history to the GDPR export.</summary>
public sealed class AssistantExporter(AppDbContext db) : IUserDataExporter
{
    public string Section => "assistant";

    public async Task<object?> ExportAsync(Guid userId, CancellationToken ct)
    {
        var conversations = await db.Set<Conversation>().AsNoTracking()
            .Where(c => c.UserId == userId)
            .Include(c => c.Messages)
            .OrderBy(c => c.LastMessageAtUtc)
            .ToListAsync(ct);

        return new
        {
            conversations = conversations.Select(c => new
            {
                c.Title,
                c.CreatedAtUtc,
                messages = c.Messages.OrderBy(m => m.Sequence).Select(m => new
                {
                    Role = m.Role.ToString(),
                    m.Content,
                    Status = m.Status.ToString(),
                    m.IsSafetyResponse,
                    m.Provider,
                    m.Model,
                    m.CreatedAtUtc,
                }),
            }),
        };
    }
}
