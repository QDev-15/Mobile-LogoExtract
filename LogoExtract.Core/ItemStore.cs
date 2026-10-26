using System.Text.Json;

namespace LogoExtract.Core;

/// <summary>File-backed store for <see cref="LogoItem"/> + <see cref="BatchGroup"/>, one folder per item
/// (<c>{root}/{itemId}/item.json</c> + its image files), same shape as DocScanner's DocumentStore. Per-item lock
/// objects in <see cref="_itemLocks"/> give mutual exclusion per item without blocking unrelated items; batches
/// are few and short-lived enough to guard with one shared lock.</summary>
public sealed class ItemStore
{
    private readonly string _root;
    private readonly object _cacheGate = new();
    private readonly Dictionary<string, LogoItem> _cache = [];
    private readonly Dictionary<string, object> _itemLocks = [];
    private readonly object _batchGate = new();
    private readonly Dictionary<string, BatchGroup> _batches = [];

    public ItemStore(string root)
    {
        _root = root;
        Directory.CreateDirectory(_root);
        LoadAll();
    }

    private void LoadAll()
    {
        if (!Directory.Exists(_root)) return;
        foreach (string dir in Directory.GetDirectories(_root))
        {
            string file = Path.Combine(dir, "item.json");
            if (!File.Exists(file)) continue;
            if (TryRead(file, out LogoItem? item) && item != null)
                lock (_cacheGate) _cache[item.Id] = item;
        }
        string batchFile = Path.Combine(_root, "batches.json");
        if (TryRead(batchFile, out List<BatchGroup>? batches) && batches != null)
            lock (_batchGate) foreach (BatchGroup b in batches) _batches[b.Id] = b;
    }

    private static bool TryRead<T>(string path, out T? value)
    {
        value = default;
        try
        {
            if (!File.Exists(path)) return false;
            value = JsonSerializer.Deserialize<T>(File.ReadAllText(path));
            return value != null;
        }
        catch (IOException) { return false; }
        catch (JsonException) { return false; }
    }

    private static void AtomicWrite(string path, string json)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    private object ItemLock(string id)
    {
        lock (_cacheGate)
        {
            if (!_itemLocks.TryGetValue(id, out object? l)) _itemLocks[id] = l = new object();
            return l;
        }
    }

    public string ItemFolder(string id) => Path.Combine(_root, id);

    /// <summary>All items, newest first. A snapshot (safe to enumerate while another thread mutates the store).</summary>
    public IReadOnlyList<LogoItem> List()
    {
        lock (_cacheGate) return [.. _cache.Values.OrderByDescending(i => i.CreatedUtc)];
    }

    public LogoItem? Get(string id)
    {
        lock (_cacheGate) return _cache.GetValueOrDefault(id);
    }

    public LogoItem Create(string batchId, string name, string originalFile)
    {
        var item = new LogoItem
        {
            Id = Guid.NewGuid().ToString("N"),
            BatchId = batchId,
            Name = name,
            CreatedUtc = DateTime.UtcNow,
            State = ItemState.Imported,
            OriginalFile = originalFile,
        };
        Directory.CreateDirectory(ItemFolder(item.Id));
        Save(item);
        return item;
    }

    /// <summary>Read-modify-write under this item's own lock; <paramref name="mutate"/> must be pure (may run
    /// again if ever retried) and must not do file I/O itself -- only field changes on the record.</summary>
    public LogoItem Update(string id, Func<LogoItem, LogoItem> mutate)
    {
        lock (ItemLock(id))
        {
            LogoItem? current = Get(id) ?? throw new InvalidOperationException($"Unknown item {id}");
            LogoItem updated = mutate(current);
            Save(updated);
            return updated;
        }
    }

    private void Save(LogoItem item)
    {
        AtomicWrite(Path.Combine(ItemFolder(item.Id), "item.json"), JsonSerializer.Serialize(item));
        lock (_cacheGate) _cache[item.Id] = item;
    }

    public void Delete(string id)
    {
        lock (ItemLock(id))
        {
            string folder = ItemFolder(id);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            // Drop the cache entry / lock-table entry LAST, after the folder is actually gone: a concurrent
            // Update(id, ...) racing in on a stale lock reference would otherwise recreate the folder with a
            // leftover doc after this Delete already removed it (same bug class fixed in DocScanner's
            // DocumentStore.Delete 2026-10-14 -- ordering matters, not just "eventually remove both").
            lock (_cacheGate) { _cache.Remove(id); _itemLocks.Remove(id); }
        }
    }

    public IReadOnlyList<BatchGroup> ListBatches()
    {
        lock (_batchGate) return [.. _batches.Values.OrderByDescending(b => b.CreatedUtc)];
    }

    public BatchGroup CreateBatch(string name)
    {
        var batch = new BatchGroup { Id = Guid.NewGuid().ToString("N"), Name = name, CreatedUtc = DateTime.UtcNow };
        lock (_batchGate)
        {
            _batches[batch.Id] = batch;
            AtomicWrite(Path.Combine(_root, "batches.json"), JsonSerializer.Serialize(_batches.Values.ToList()));
        }
        return batch;
    }
}
