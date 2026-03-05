using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

/// <summary>
/// Shared helpers so commands don't duplicate JsonElement parsing.
/// Assumes IRuntimeTransport.ExecuteAsync returns JsonElement.
/// </summary>
public static class CommandJson
{
    public static string? TryGetResultMessage(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object)
            return null;

        if (TryGetError(response, out var errorCode, out var errorMessage))
        {
            if (!string.IsNullOrWhiteSpace(errorCode) && !string.IsNullOrWhiteSpace(errorMessage))
                return $"Error ({errorCode}): {errorMessage}";
            if (!string.IsNullOrWhiteSpace(errorMessage))
                return $"Error: {errorMessage}";
            if (!string.IsNullOrWhiteSpace(errorCode))
                return $"Error ({errorCode})";
        }

        // If API returned a message, return it
        if (response.TryGetProperty("message", out var msg) &&
            msg.ValueKind == JsonValueKind.String)
        {
            return msg.GetString();
        }

        if (TryGetString(response, "action", out var action))
        {
            if (action == "arrived")
            {
                if (TryGetString(response, "poi", out var poi))
                    return $"Arrived at {poi}.";
                return "Arrived.";
            }

            if (action == "dock")
            {
                if (TryGetString(response, "base", out var b))
                    return $"Docked at {b}.";
                return "Docked.";
            }

            if (action == "undock")
                return "Departed station.";

            if (action == "refuel")
            {
                var parts = new List<string>();
                if (TryGetInt(response, "cost", out var cost))
                    parts.Add($"cost {cost}");
                if (TryGetInt(response, "fuel", out var fuel) &&
                    TryGetInt(response, "max_fuel", out var maxFuel))
                {
                    parts.Add($"fuel {fuel}/{maxFuel}");
                }

                return parts.Count > 0
                    ? $"Refueled ({string.Join(", ", parts)})."
                    : "Refueled.";
            }

            if (action == "deposit_credits")
            {
                if (TryGetInt(response, "amount", out var amount))
                    return $"Deposited {amount} credits.";
                return "Deposited credits.";
            }

            if (action == "withdraw_credits")
            {
                if (TryGetInt(response, "amount", out var amount))
                    return $"Withdrew {amount} credits.";
                return "Withdrew credits.";
            }

            if (action == "deposit_items")
            {
                var item = TryGetString(response, "item_id", out var itemId) ? itemId : "item";
                var qtyText = TryGetInt(response, "amount", out var amount)
                    ? amount.ToString()
                    : (TryGetInt(response, "quantity", out var quantity) ? quantity.ToString() : "?");
                return $"Deposited {qtyText}x {item}.";
            }

            if (action == "withdraw_items")
            {
                var item = TryGetString(response, "item_id", out var itemId) ? itemId : "item";
                var qtyText = TryGetInt(response, "amount", out var amount)
                    ? amount.ToString()
                    : (TryGetInt(response, "quantity", out var quantity) ? quantity.ToString() : "?");
                return $"Withdrew {qtyText}x {item}.";
            }

            if (action == "create_sell_order")
            {
                var item = TryGetString(response, "item_id", out var itemId) ? itemId : "item";
                var qtyText = TryGetInt(response, "quantity", out var qty) ? qty.ToString() : "?";
                var priceText = TryGetDecimal(response, "price_each", out var price) ? price.ToString("0.##") : "?";
                return $"Sell order: {qtyText}x {item} @ {priceText}.";
            }

            if (action == "create_buy_order")
            {
                var item = TryGetString(response, "item_id", out var itemId) ? itemId : "item";
                var qtyText = TryGetInt(response, "quantity", out var qty) ? qty.ToString() : "?";
                var priceText = TryGetDecimal(response, "price_each", out var price) ? price.ToString("0.##") : "?";
                return $"Buy order: {qtyText}x {item} @ {priceText}.";
            }


            var detail = BuildCompactDetail(response, new[] { "action" });
            return string.IsNullOrWhiteSpace(detail)
                ? $"{action}."
                : $"{action}: {detail}.";
        }

        // Otherwise fallback to compact JSON
        return response.GetRawText();
    }

    public static bool TryGetError(JsonElement response, out string? code, out string? message)
    {
        code = null;
        message = null;

        if (response.ValueKind != JsonValueKind.Object)
            return false;

        if (!response.TryGetProperty("error", out var error) ||
            error.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (error.TryGetProperty("code", out var codeEl) &&
            codeEl.ValueKind == JsonValueKind.String)
        {
            code = codeEl.GetString();
        }

        if (error.TryGetProperty("message", out var msgEl) &&
            msgEl.ValueKind == JsonValueKind.String)
        {
            message = msgEl.GetString();
        }

        return true;
    }

    private static bool TryGetString(JsonElement obj, string name, out string value)
    {
        value = "";
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String)
            return false;

        value = el.GetString() ?? "";
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryGetInt(JsonElement obj, string name, out int value)
    {
        value = 0;
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Number)
            return false;

        return el.TryGetInt32(out value);
    }

    private static bool TryGetDecimal(JsonElement obj, string name, out decimal value)
    {
        value = 0;
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Number)
            return false;

        return el.TryGetDecimal(out value);
    }

    private static string BuildCompactDetail(JsonElement obj, IEnumerable<string> excludeKeys)
    {
        var exclude = new HashSet<string>(excludeKeys, StringComparer.OrdinalIgnoreCase);
        var parts = new List<string>();

        foreach (var prop in obj.EnumerateObject())
        {
            if (exclude.Contains(prop.Name))
                continue;

            if (prop.Value.ValueKind == JsonValueKind.String)
                parts.Add($"{prop.Name}={prop.Value.GetString()}");
            else if (prop.Value.ValueKind == JsonValueKind.Number)
                parts.Add($"{prop.Name}={prop.Value}");
            else if (prop.Value.ValueKind == JsonValueKind.True || prop.Value.ValueKind == JsonValueKind.False)
                parts.Add($"{prop.Name}={prop.Value.GetBoolean()}");

            if (parts.Count >= 4)
                break;
        }

        return string.Join(", ", parts);
    }
}

// =====================================================
// MINE
// =====================================================

