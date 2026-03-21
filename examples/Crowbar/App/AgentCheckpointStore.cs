using System;
using System.IO;
using System.Text.Json;

public sealed class AgentCheckpointStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public CommandExecutionCheckpoint? Load(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
                return null;

            var raw = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<CommandExecutionCheckpoint>(raw);
        }
        catch
        {
            return null;
        }
    }

    public void Save(string filePath, CommandExecutionCheckpoint checkpoint)
    {
        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var payload = checkpoint with
            {
                SavedAtUtc = DateTime.UtcNow
            };

            var json = JsonSerializer.Serialize(payload, JsonOptions);
            var tempPath = $"{filePath}.tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, filePath, overwrite: true);
        }
        catch
        {
            // Checkpointing is best-effort.
        }
    }
}
