using System;
using System.IO;
using System.Text.Json;

public sealed class SavedLlmSelectionStore
{
    public SavedLlmSelection? Load()
    {
        try
        {
            if (!File.Exists(AppPaths.SavedLlmSelectionFile))
                return null;

            var raw = File.ReadAllText(AppPaths.SavedLlmSelectionFile);
            var loaded = JsonSerializer.Deserialize<SavedLlmSelection>(raw);
            if (loaded == null)
                return null;

            var provider = (loaded.Provider ?? string.Empty).Trim().ToLowerInvariant();
            var model = (loaded.Model ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model))
                return null;

            return new SavedLlmSelection(provider, model);
        }
        catch
        {
            return null;
        }
    }

    public void Save(string provider, string model)
    {
        var normalizedProvider = (provider ?? string.Empty).Trim().ToLowerInvariant();
        var normalizedModel = (model ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedProvider) || string.IsNullOrWhiteSpace(normalizedModel))
            return;

        var json = JsonSerializer.Serialize(
            new SavedLlmSelection(normalizedProvider, normalizedModel),
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(AppPaths.SavedLlmSelectionFile, json);
    }
}
