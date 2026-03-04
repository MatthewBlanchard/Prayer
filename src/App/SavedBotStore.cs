using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

public sealed class SavedBotStore
{
    public List<SavedBot> Load()
    {
        try
        {
            if (!File.Exists(AppPaths.SavedBotsFile))
                return new List<SavedBot>();

            var raw = File.ReadAllText(AppPaths.SavedBotsFile);
            var loaded = JsonSerializer.Deserialize<List<SavedBot>>(raw);
            return loaded?
                .Where(b =>
                    !string.IsNullOrWhiteSpace(b.Username) &&
                    !string.IsNullOrWhiteSpace(b.Password))
                .ToList() ?? new List<SavedBot>();
        }
        catch
        {
            return new List<SavedBot>();
        }
    }

    public void Save(List<SavedBot> bots)
    {
        var cleaned = bots
            .Where(b =>
                !string.IsNullOrWhiteSpace(b.Username) &&
                !string.IsNullOrWhiteSpace(b.Password))
            .Select(b => new SavedBot(
                b.Username.Trim(),
                b.Password))
            .ToList();

        var json = JsonSerializer.Serialize(cleaned, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(AppPaths.SavedBotsFile, json);
    }

    public void Upsert(List<SavedBot> bots, string username, string password)
    {
        var normalizedUsername = username.Trim();
        var existing = bots.FindIndex(b =>
            string.Equals(b.Username, normalizedUsername, StringComparison.OrdinalIgnoreCase));

        if (existing >= 0)
        {
            bots[existing] = new SavedBot(normalizedUsername, password);
        }
        else
        {
            bots.Add(new SavedBot(normalizedUsername, password));
        }

        Save(bots);
    }
}
