using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

internal sealed class SpaceMoltSessionCache
{
    private static readonly object FileLock = new();

    public bool TryGet(string username, out SpaceMoltSessionInfo session)
    {
        session = default!;
        if (string.IsNullOrWhiteSpace(username))
            return false;

        lock (FileLock)
        {
            var entries = LoadEntriesUnsafe();
            if (!entries.TryGetValue(NormalizeKey(username), out var entry))
                return false;

            if (string.IsNullOrWhiteSpace(entry.Id))
                return false;

            session = new SpaceMoltSessionInfo
            {
                Id = entry.Id,
                CreatedAt = entry.CreatedAt,
                ExpiresAt = entry.ExpiresAt
            };
            return true;
        }
    }

    public void Upsert(string username, SpaceMoltSessionInfo session)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(session?.Id))
            return;

        lock (FileLock)
        {
            var entries = LoadEntriesUnsafe();
            entries[NormalizeKey(username)] = new Entry
            {
                Id = session.Id,
                CreatedAt = session.CreatedAt,
                ExpiresAt = session.ExpiresAt
            };
            SaveEntriesUnsafe(entries);
        }
    }

    public void Remove(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return;

        lock (FileLock)
        {
            var entries = LoadEntriesUnsafe();
            if (!entries.Remove(NormalizeKey(username)))
                return;

            SaveEntriesUnsafe(entries);
        }
    }

    private static Dictionary<string, Entry> LoadEntriesUnsafe()
    {
        try
        {
            if (!File.Exists(AppPaths.SpaceMoltSessionsFile))
                return new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

            var raw = File.ReadAllText(AppPaths.SpaceMoltSessionsFile);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, Entry>>(raw);

            if (loaded == null)
                return new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

            return loaded
                .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key) && !string.IsNullOrWhiteSpace(kvp.Value?.Id))
                .ToDictionary(
                    kvp => kvp.Key.Trim(),
                    kvp => kvp.Value!,
                    StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void SaveEntriesUnsafe(Dictionary<string, Entry> entries)
    {
        var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(AppPaths.SpaceMoltSessionsFile, json);
    }

    private static string NormalizeKey(string username)
        => username.Trim().ToLowerInvariant();

    private sealed class Entry
    {
        public string Id { get; set; } = "";
        public DateTimeOffset? CreatedAt { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; }
    }
}
