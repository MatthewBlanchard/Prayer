using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

internal sealed class SpaceMoltNotificationTracker
{
    private readonly List<GameNotification> _pendingNotifications = new();
    private readonly List<GameChatMessage> _recentChatMessages = new();
    private readonly int _maxQueuedNotifications;
    private readonly int _maxChatMessages;

    public SpaceMoltNotificationTracker(int maxQueuedNotifications, int maxChatMessages)
    {
        _maxQueuedNotifications = Math.Max(1, maxQueuedNotifications);
        _maxChatMessages = Math.Max(1, maxChatMessages);
    }

    public void ObservePayload(JsonElement payload, ref int currentTick)
    {
        ObserveTickFromPayload(payload, ref currentTick);
        QueueNotifications(payload, currentTick);
    }

    public bool ObserveTickFromPayload(JsonElement payload, ref int currentTick)
    {
        int? parsedTick = TryExtractTick(payload);
        if (!parsedTick.HasValue)
            return false;

        currentTick = Math.Max(currentTick, parsedTick.Value);
        return true;
    }

    public GameNotification[] DrainPendingNotifications(int maxCount)
    {
        if (_pendingNotifications.Count == 0)
            return Array.Empty<GameNotification>();

        var latest = _pendingNotifications
            .TakeLast(Math.Max(0, maxCount))
            .ToArray();

        _pendingNotifications.Clear();
        return latest;
    }

    public GameChatMessage[] SnapshotChatMessages(int maxCount)
    {
        if (_recentChatMessages.Count == 0 || maxCount <= 0)
            return Array.Empty<GameChatMessage>();

        return _recentChatMessages
            .TakeLast(maxCount)
            .Select(m => new GameChatMessage
            {
                MessageId = m.MessageId,
                Channel = m.Channel,
                Sender = m.Sender,
                Content = m.Content,
                SeenTick = m.SeenTick
            })
            .ToArray();
    }

    private void QueueNotifications(JsonElement content, int currentTick)
    {
        if (content.ValueKind != JsonValueKind.Object)
            return;

        if (!content.TryGetProperty("notifications", out var notifications) ||
            notifications.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var notification in notifications.EnumerateArray())
        {
            if (notification.ValueKind != JsonValueKind.Object)
                continue;

            string type = SpaceMoltJson.TryGetString(notification, "type") ?? "notification";
            string summary = BuildNotificationSummary(notification);
            string payloadJson = notification.GetRawText();

            _pendingNotifications.Add(new GameNotification
            {
                Type = type,
                Summary = summary,
                PayloadJson = payloadJson
            });

            TrackChatNotification(notification, currentTick);
        }

        if (_pendingNotifications.Count > _maxQueuedNotifications)
        {
            int removeCount = _pendingNotifications.Count - _maxQueuedNotifications;
            _pendingNotifications.RemoveRange(0, removeCount);
        }
    }

    private static string BuildNotificationSummary(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return "notification";

        string type = SpaceMoltJson.TryGetString(payload, "type") ?? "notification";
        string msgType = SpaceMoltJson.TryGetString(payload, "msg_type") ?? "";

        JsonElement data = payload;
        if (payload.TryGetProperty("data", out var dataElement) &&
            dataElement.ValueKind == JsonValueKind.Object)
        {
            data = dataElement;
        }
        else if (payload.TryGetProperty("payload", out var payloadElement) &&
                 payloadElement.ValueKind == JsonValueKind.Object)
        {
            data = payloadElement;
        }

        if (string.Equals(msgType, "chat_message", StringComparison.OrdinalIgnoreCase))
        {
            string sender = SpaceMoltJson.TryGetString(data, "sender") ?? "unknown";
            string content = SpaceMoltJson.TryGetString(data, "content") ?? "";
            string channel = SpaceMoltJson.TryGetString(data, "channel") ?? "";

            if (!string.IsNullOrWhiteSpace(channel))
                return string.IsNullOrWhiteSpace(content)
                    ? $"chat({channel}): {sender}"
                    : $"chat({channel}): {sender}: {content}";

            return string.IsNullOrWhiteSpace(content)
                ? $"chat: {sender}"
                : $"chat: {sender}: {content}";
        }

        if (string.Equals(msgType, "pirate_warning", StringComparison.OrdinalIgnoreCase))
        {
            string pirate = SpaceMoltJson.TryGetString(data, "pirate_name") ?? "Pirate";
            string message = SpaceMoltJson.TryGetString(data, "message") ?? "";
            return string.IsNullOrWhiteSpace(message)
                ? $"warning: {pirate}"
                : $"warning: {pirate}: {message}";
        }

        string? topMessage = SpaceMoltJson.TryGetString(payload, "message");
        if (!string.IsNullOrWhiteSpace(topMessage))
            return $"{type}: {topMessage}";

        string? dataMessage = SpaceMoltJson.TryGetString(data, "message");
        if (!string.IsNullOrWhiteSpace(dataMessage))
            return $"{type}: {dataMessage}";

        if (string.Equals(type, "mining_yield", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(msgType, "mining_yield", StringComparison.OrdinalIgnoreCase))
        {
            string resource = SpaceMoltJson.TryGetString(data, "resource_id") ?? "resource";
            int? quantity = SpaceMoltJson.TryGetInt(data, "quantity");
            return quantity.HasValue
                ? $"mining_yield: {resource} x{quantity.Value}"
                : $"mining_yield: {resource}";
        }

        string compact = BuildCompactDetail(data, maxFields: 3);
        if (!string.IsNullOrWhiteSpace(compact))
            return $"{(string.IsNullOrWhiteSpace(msgType) ? type : msgType)}: {compact}";

        return string.IsNullOrWhiteSpace(msgType)
            ? type
            : msgType;
    }

    private void TrackChatNotification(JsonElement payload, int currentTick)
    {
        if (!TryParseChatMessage(payload, out var chat))
            return;

        if (!string.IsNullOrWhiteSpace(chat.MessageId) &&
            _recentChatMessages.Any(m => string.Equals(m.MessageId, chat.MessageId, StringComparison.Ordinal)))
        {
            return;
        }

        chat.SeenTick = currentTick;
        _recentChatMessages.Add(chat);

        if (_recentChatMessages.Count > _maxChatMessages)
        {
            int removeCount = _recentChatMessages.Count - _maxChatMessages;
            _recentChatMessages.RemoveRange(0, removeCount);
        }
    }

    private static bool TryParseChatMessage(JsonElement payload, out GameChatMessage chat)
    {
        chat = new GameChatMessage();
        if (payload.ValueKind != JsonValueKind.Object)
            return false;

        string type = SpaceMoltJson.TryGetString(payload, "type") ?? "";
        string msgType = SpaceMoltJson.TryGetString(payload, "msg_type") ?? "";

        if (!string.Equals(msgType, "chat_message", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(type, "chat_message", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        JsonElement data = payload;
        if (payload.TryGetProperty("data", out var dataElement) &&
            dataElement.ValueKind == JsonValueKind.Object)
        {
            data = dataElement;
        }
        else if (payload.TryGetProperty("payload", out var payloadElement) &&
                 payloadElement.ValueKind == JsonValueKind.Object)
        {
            data = payloadElement;
        }

        chat = new GameChatMessage
        {
            MessageId = SpaceMoltJson.TryGetString(data, "id") ?? "",
            Channel = SpaceMoltJson.TryGetString(data, "channel") ?? "",
            Sender = SpaceMoltJson.TryGetString(data, "sender") ?? "unknown",
            Content = SpaceMoltJson.TryGetString(data, "content") ?? ""
        };

        return true;
    }

    private static int? TryExtractTick(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return null;

        int? topTick = SpaceMoltJson.TryGetInt(payload, "current_tick", "tick");
        if (topTick.HasValue)
            return topTick;

        if (payload.TryGetProperty("result", out var result) &&
            result.ValueKind == JsonValueKind.Object)
        {
            int? resultTick = SpaceMoltJson.TryGetInt(result, "current_tick", "tick", "arrival_tick");
            if (resultTick.HasValue)
                return resultTick;
        }

        if (payload.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Object)
        {
            int? dataTick = SpaceMoltJson.TryGetInt(data, "current_tick", "tick");
            if (dataTick.HasValue)
                return dataTick;
        }

        return null;
    }

    private static string BuildCompactDetail(JsonElement payload, int maxFields)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return "";

        var details = new List<string>();
        foreach (var prop in payload.EnumerateObject())
        {
            if (details.Count >= maxFields)
                break;

            string? value = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString(),
                JsonValueKind.Number => prop.Value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null
            };

            if (string.IsNullOrWhiteSpace(value))
                continue;

            details.Add($"{prop.Name}={value}");
        }

        return string.Join(", ", details);
    }
}
