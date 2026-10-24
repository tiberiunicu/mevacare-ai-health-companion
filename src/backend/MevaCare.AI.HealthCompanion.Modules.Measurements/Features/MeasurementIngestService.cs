using MevaCare.AI.HealthCompanion.Contracts.Measurements;
using MevaCare.AI.HealthCompanion.Infrastructure.Persistence;
using MevaCare.AI.HealthCompanion.Modules.Measurements.Domain;
using Microsoft.EntityFrameworkCore;

namespace MevaCare.AI.HealthCompanion.Modules.Measurements.Features;

/// <summary>
/// Stores normalized provider data. Matching is by (source, external id), so repeating an import — or importing an overlapping time
/// window — updates the existing rows (for example a sleep that was "pending score" and is scored now) and never duplicates them.
/// </summary>
public sealed class MeasurementIngestService(AppDbContext db) : IMeasurementIngest
{
    private const int LookupChunk = 500;

    public async Task<IngestCounts> UpsertAsync(Guid userId, string provider, NormalizedHealthData data, CancellationToken ct)
    {
        provider = NormalizeProvider(provider);
        if (data.Count == 0)
        {
            return new IngestCounts(0, 0);
        }

        var source = await GetOrCreateSourceAsync(userId, provider, ct);
        var added = 0;
        var updated = 0;

        // ---- single-value measurements ----
        var measurementInput = data.Measurements
            .GroupBy(m => (m.Metric, m.ExternalId))
            .Select(g => g.Last())
            .ToList();
        var existing = new Dictionary<(HealthMetric, string), HealthMeasurement>();
        foreach (var chunk in measurementInput.Select(m => m.ExternalId).Distinct().Chunk(LookupChunk))
        {
            var rows = await db.Set<HealthMeasurement>()
                .Where(m => m.SourceId == source.Id && m.ExternalId != null && chunk.Contains(m.ExternalId))
                .ToListAsync(ct);
            foreach (var row in rows)
            {
                existing[(row.Metric, row.ExternalId!)] = row;
            }
        }

        foreach (var item in measurementInput)
        {
            if (existing.TryGetValue((item.Metric, item.ExternalId), out var row))
            {
                row.Apply(item.Value, item.MeasuredAt);
                updated++;
            }
            else
            {
                db.Add(new HealthMeasurement(userId, source.Id, item.Metric, item.Value, item.MeasuredAt, item.ExternalId));
                added++;
            }
        }

        // ---- sleep sessions ----
        var sleepInput = data.Sleeps.GroupBy(s => s.ExternalId).Select(g => g.Last()).ToList();
        var sleeps = new Dictionary<string, SleepSession>();
        foreach (var chunk in sleepInput.Select(s => s.ExternalId).Chunk(LookupChunk))
        {
            foreach (var row in await db.Set<SleepSession>().Where(s => s.SourceId == source.Id && chunk.Contains(s.ExternalId)).ToListAsync(ct))
            {
                sleeps[row.ExternalId] = row;
            }
        }

        foreach (var item in sleepInput)
        {
            if (sleeps.TryGetValue(item.ExternalId, out var row))
            {
                row.Apply(item);
                updated++;
            }
            else
            {
                db.Add(new SleepSession(userId, source.Id, item));
                added++;
            }
        }

        // ---- workouts ----
        var workoutInput = data.Workouts.GroupBy(w => w.ExternalId).Select(g => g.Last()).ToList();
        var workouts = new Dictionary<string, Workout>();
        foreach (var chunk in workoutInput.Select(w => w.ExternalId).Chunk(LookupChunk))
        {
            foreach (var row in await db.Set<Workout>().Where(w => w.SourceId == source.Id && chunk.Contains(w.ExternalId)).ToListAsync(ct))
            {
                workouts[row.ExternalId] = row;
            }
        }

        foreach (var item in workoutInput)
        {
            if (workouts.TryGetValue(item.ExternalId, out var row))
            {
                row.Apply(item);
                updated++;
            }
            else
            {
                db.Add(new Workout(userId, source.Id, item));
                added++;
            }
        }

        await db.SaveChangesAsync(ct);
        return new IngestCounts(added, updated);
    }

    public async Task<int> DeleteProviderDataAsync(Guid userId, string provider, CancellationToken ct)
    {
        provider = NormalizeProvider(provider);
        if (provider == HealthMetrics.ManualProvider)
        {
            throw new ArgumentException("Manual entries are not provider data.", nameof(provider));
        }

        var source = await db.Set<MeasurementSource>().SingleOrDefaultAsync(s => s.UserId == userId && s.Provider == provider, ct);
        if (source is null)
        {
            return 0;
        }

        var removed = await db.Set<HealthMeasurement>().Where(m => m.SourceId == source.Id).CountAsync(ct)
            + await db.Set<SleepSession>().Where(s => s.SourceId == source.Id).CountAsync(ct)
            + await db.Set<Workout>().Where(w => w.SourceId == source.Id).CountAsync(ct);

        // Rows go with their source (cascade).
        db.Remove(source);
        await db.SaveChangesAsync(ct);
        return removed;
    }

    internal static string NormalizeProvider(string provider) => provider.Trim().ToLowerInvariant();

    internal async Task<MeasurementSource> GetOrCreateSourceAsync(Guid userId, string provider, CancellationToken ct)
    {
        var source = await db.Set<MeasurementSource>().SingleOrDefaultAsync(s => s.UserId == userId && s.Provider == provider, ct);
        if (source is not null)
        {
            return source;
        }

        source = new MeasurementSource(userId, provider);
        db.Add(source);
        return source;
    }
}
