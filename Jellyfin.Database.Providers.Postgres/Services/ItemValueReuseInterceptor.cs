using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Providers.Postgres.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Jellyfin.Database.Providers.Postgres.Services;

/// <summary>
/// EF Core interceptor that reuses an existing <c>ItemValues</c> row when Jellyfin is about to insert a
/// genre, tag, studio or artist that is already in the database.
/// </summary>
/// <remarks>
/// <para>
/// Jellyfin computes the values it is missing with a SELECT and then inserts the difference. When that
/// SELECT and the INSERT disagree — another task saved the same value in between, or the read was answered
/// from an older snapshot — PostgreSQL rejects the insert with 23505 on <c>IX_ItemValues_Type_Value</c> and
/// the whole batch is rolled back. The batch is not only the value: <c>BaseItems</c>, its images and its
/// mappings travel with it, which is how channel items ended up missing from the database while Jellyfin
/// kept reporting them.
/// </para>
/// <para>
/// The outcome is made deterministic here: the pending <c>ItemValues</c> row is dropped, the id of the row
/// that already exists is used instead, and the <c>ItemValuesMap</c> rows are repointed so the item keeps
/// its genre or tag. Nothing is lost and nothing fails.
/// </para>
/// </remarks>
public sealed class ItemValueReuseInterceptor : SaveChangesInterceptor
{
    /// <summary>Reported once per process: repeats would flood the log during a full library scan.</summary>
    private static int _announced;

    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        ReuseExisting(eventData.Context);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ReuseExisting(eventData.Context);
        return ValueTask.FromResult(result);
    }

    /// <summary>
    /// Rewrites the pending changes so no <c>INSERT INTO "ItemValues"</c> can collide with the unique index.
    /// </summary>
    /// <param name="context">Context about to save; may be null.</param>
    /// <returns>Number of pending values that were replaced by an existing row.</returns>
    internal static int ReuseExisting(DbContext? context)
    {
        if (context is null)
        {
            return 0;
        }

        var tracker = context.ChangeTracker;
        var pending = CollectPending(tracker);
        if (pending.Count == 0)
        {
            return 0;
        }

        var existing = FindExisting(context, pending);
        if (existing.Count == 0)
        {
            return 0;
        }

        var remapped = PlanRemapping(pending, existing);
        if (remapped.Count == 0)
        {
            return 0;
        }

        // The mappings are rewired first: detaching a value makes EF detach the mappings it fixed up to it,
        // and then there would be nothing left to repoint.
        RepointMappings(context, remapped);
        DetachValues(pending, remapped);

        if (Interlocked.Increment(ref _announced) == 1)
        {
            PostgresLog.Info(
                $"[ItemValues] {remapped.Count} valor(es) ya existentes reutilizados en lugar de insertarlos: " +
                "el lote no se pierde con 23505.");
        }

        return remapped.Count;
    }

    /// <summary>Groups the values EF is about to insert by the pair the unique index protects.</summary>
    /// <param name="tracker">Change tracker of the context being saved.</param>
    /// <returns>Pending values by <c>(Type, Value)</c>.</returns>
    private static Dictionary<(ItemValueType Type, string Value), List<EntityEntry<ItemValue>>> CollectPending(
        ChangeTracker tracker)
    {
        var pending = new Dictionary<(ItemValueType Type, string Value), List<EntityEntry<ItemValue>>>();
        foreach (var entry in tracker.Entries<ItemValue>())
        {
            if (entry.State != EntityState.Added)
            {
                continue;
            }

            var key = (entry.Entity.Type, entry.Entity.Value);
            if (!pending.TryGetValue(key, out var entries))
            {
                entries = [];
                pending[key] = entries;
            }

            entries.Add(entry);
        }

        return pending;
    }

    /// <summary>Reads the ids of the pairs that are already stored.</summary>
    /// <param name="context">Context being saved.</param>
    /// <param name="pending">Pairs that are about to be inserted.</param>
    /// <returns>Id already stored for each colliding pair.</returns>
    private static Dictionary<(ItemValueType Type, string Value), Guid> FindExisting(
        DbContext context,
        Dictionary<(ItemValueType Type, string Value), List<EntityEntry<ItemValue>>> pending)
    {
        var types = pending.Keys.Select(key => key.Type).Distinct().ToArray();
        var values = pending.Keys.Select(key => key.Value).Distinct().ToArray();

        // The pair filter is applied in memory because the SQL side would compare every type with every
        // value; the comparison itself stays exactly as case sensitive as the one Jellyfin makes.
        var rows = context.Set<ItemValue>()
            .AsNoTracking()
            .TagWith(HomeQueryCacheInterceptor.NoCacheMarker)
            .Where(value => types.Contains(value.Type) && values.Contains(value.Value))
            .Select(value => new { value.Type, value.Value, value.ItemValueId })
            .AsEnumerable()
            .Where(value => pending.ContainsKey((value.Type, value.Value)))
            .ToList();

        var existing = new Dictionary<(ItemValueType Type, string Value), Guid>(rows.Count);
        foreach (var row in rows)
        {
            existing[(row.Type, row.Value)] = row.ItemValueId;
        }

        return existing;
    }

    /// <summary>Decides which pending id has to be replaced, without touching the change tracker yet.</summary>
    /// <param name="pending">Pairs that are about to be inserted.</param>
    /// <param name="existing">Id already stored for each colliding pair.</param>
    /// <returns>Pending id to stored id.</returns>
    private static Dictionary<Guid, Guid> PlanRemapping(
        Dictionary<(ItemValueType Type, string Value), List<EntityEntry<ItemValue>>> pending,
        Dictionary<(ItemValueType Type, string Value), Guid> existing)
    {
        var remapped = new Dictionary<Guid, Guid>();
        foreach (var (key, entries) in pending)
        {
            if (!existing.TryGetValue(key, out var storedId))
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (entry.Entity.ItemValueId != storedId)
                {
                    remapped[entry.Entity.ItemValueId] = storedId;
                }
            }
        }

        return remapped;
    }

    /// <summary>Drops the pending rows whose value is already stored.</summary>
    /// <param name="pending">Pairs that are about to be inserted.</param>
    /// <param name="remapped">Pending id to stored id.</param>
    private static void DetachValues(
        Dictionary<(ItemValueType Type, string Value), List<EntityEntry<ItemValue>>> pending,
        Dictionary<Guid, Guid> remapped)
    {
        foreach (var entries in pending.Values)
        {
            foreach (var entry in entries)
            {
                if (remapped.ContainsKey(entry.Entity.ItemValueId))
                {
                    entry.State = EntityState.Detached;
                }
            }
        }
    }

    /// <summary>Points the mappings of the dropped values at the row that already exists.</summary>
    /// <param name="context">Context being saved.</param>
    /// <param name="remapped">Pending id to stored id.</param>
    private static void RepointMappings(DbContext context, Dictionary<Guid, Guid> remapped)
    {
        var mappings = context.ChangeTracker.Entries<ItemValueMap>()
            .Where(entry => entry.State == EntityState.Added)
            .Select(entry => entry.Entity)
            .ToList();

        var present = new HashSet<(Guid ItemValueId, Guid ItemId)>(mappings.Count);
        foreach (var mapping in mappings)
        {
            present.Add((mapping.ItemValueId, mapping.ItemId));
        }

        var repointed = new List<(Guid ItemValueId, Guid ItemId)>();
        foreach (var mapping in mappings)
        {
            if (!remapped.TryGetValue(mapping.ItemValueId, out var storedId))
            {
                continue;
            }

            context.Entry(mapping).State = EntityState.Detached;
            present.Remove((mapping.ItemValueId, mapping.ItemId));

            // A mapping may already point at the stored row, and two dropped values can share it: only the
            // first one is added, or the primary key of ItemValuesMap would be inserted twice.
            if (present.Add((storedId, mapping.ItemId)))
            {
                repointed.Add((storedId, mapping.ItemId));
            }
        }

        foreach (var (itemValueId, itemId) in repointed)
        {
            context.Set<ItemValueMap>().Add(new ItemValueMap
            {
                ItemId = itemId,
                ItemValueId = itemValueId,
                Item = null!,
                ItemValue = null!,
            });
        }
    }
}
