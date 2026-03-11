using System;
using System.Collections.Generic;
using System.Text.Json;

internal static class SpaceMoltResponseParsers
{
    public static bool TryParseSkills(JsonElement response, out Dictionary<string, int> skills)
    {
        skills = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (response.ValueKind != JsonValueKind.Object)
            return false;

        JsonElement root = response;
        if (response.TryGetProperty("result", out var wrappedResult) &&
            wrappedResult.ValueKind == JsonValueKind.Object)
        {
            root = wrappedResult;
        }

        JsonElement skillContainer = default;
        if (root.TryGetProperty("skills", out var directSkills))
        {
            skillContainer = directSkills;
        }
        else if (root.TryGetProperty("player", out var playerObj) &&
                 playerObj.ValueKind == JsonValueKind.Object &&
                 playerObj.TryGetProperty("skills", out var playerSkills))
        {
            skillContainer = playerSkills;
        }
        else
        {
            return false;
        }

        ParseSkillsInto(skillContainer, skills);
        return true;
    }

    private static void ParseSkillsInto(JsonElement source, Dictionary<string, int> destination)
    {
        if (source.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in source.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(property.Name))
                    continue;

                if (TryReadSkillLevel(property.Value, out var level))
                    destination[property.Name] = level;
            }
            return;
        }

        if (source.ValueKind != JsonValueKind.Array)
            return;

        foreach (var entry in source.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                continue;

            var skillId =
                SpaceMoltJson.TryGetString(entry, "skill_id") ??
                SpaceMoltJson.TryGetString(entry, "id") ??
                SpaceMoltJson.TryGetString(entry, "name");
            if (string.IsNullOrWhiteSpace(skillId))
                continue;

            if (!TryReadSkillLevel(entry, out var level))
                continue;

            destination[skillId] = level;
        }
    }

    private static bool TryReadSkillLevel(JsonElement value, out int level)
    {
        level = 0;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            level = number;
            return true;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            int? objectLevel =
                SpaceMoltJson.TryGetInt(value, "level") ??
                SpaceMoltJson.TryGetInt(value, "skill_level") ??
                SpaceMoltJson.TryGetInt(value, "current_level");
            if (objectLevel.HasValue)
            {
                level = objectLevel.Value;
                return true;
            }
        }

        return false;
    }

    public static ShipyardShowroomEntry[] ParseShipyardShowroom(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object)
            return Array.Empty<ShipyardShowroomEntry>();

        JsonElement result = response;
        if (response.TryGetProperty("result", out var wrappedResult) &&
            wrappedResult.ValueKind == JsonValueKind.Object)
        {
            result = wrappedResult;
        }

        JsonElement shipsArray = default;
        bool found =
            (result.TryGetProperty("ships", out shipsArray) && shipsArray.ValueKind == JsonValueKind.Array) ||
            (result.TryGetProperty("listings", out shipsArray) && shipsArray.ValueKind == JsonValueKind.Array);
        if (!found)
            return Array.Empty<ShipyardShowroomEntry>();

        var entries = new List<ShipyardShowroomEntry>();
        foreach (var ship in shipsArray.EnumerateArray())
        {
            if (ship.ValueKind != JsonValueKind.Object)
                continue;

            string name = SpaceMoltJson.TryGetString(ship, "name") ?? "";
            string classId =
                SpaceMoltJson.TryGetString(ship, "class_id") ??
                SpaceMoltJson.TryGetString(ship, "ship_class") ??
                "";
            string? shipId = SpaceMoltJson.TryGetString(ship, "ship_id");
            string category = SpaceMoltJson.TryGetString(ship, "category") ?? "";
            int? tier = SpaceMoltJson.TryGetInt(ship, "tier");
            int? scale = SpaceMoltJson.TryGetInt(ship, "scale");

            int? hull = SpaceMoltJson.TryGetInt(ship, "hull", "base_hull");
            int? shield = SpaceMoltJson.TryGetInt(ship, "shield", "base_shield");
            int? cargo = SpaceMoltJson.TryGetInt(ship, "cargo", "cargo_capacity");
            int? speed = SpaceMoltJson.TryGetInt(ship, "speed", "base_speed");

            decimal? price =
                (ship.TryGetProperty("showroom_price", out var p0) && p0.ValueKind == JsonValueKind.Number && p0.TryGetDecimal(out var d0)) ? d0 :
                (ship.TryGetProperty("price", out var p1) && p1.ValueKind == JsonValueKind.Number && p1.TryGetDecimal(out var d1)) ? d1 :
                null;

            entries.Add(new ShipyardShowroomEntry
            {
                ShipClassId = classId,
                ShipId = shipId,
                Name = name,
                Category = category,
                Tier = tier,
                Scale = scale,
                Hull = hull,
                Shield = shield,
                Cargo = cargo,
                Speed = speed,
                Price = price
            });
            if (entries.Count >= 12)
                break;
        }

        return entries.ToArray();
    }

    public static ShipyardListingEntry[] ParseShipyardListings(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object)
            return Array.Empty<ShipyardListingEntry>();

        JsonElement result = response;
        if (response.TryGetProperty("result", out var wrappedResult) &&
            wrappedResult.ValueKind == JsonValueKind.Object)
        {
            result = wrappedResult;
        }

        JsonElement listingsArray = default;
        bool found =
            (result.TryGetProperty("listings", out listingsArray) && listingsArray.ValueKind == JsonValueKind.Array) ||
            (result.TryGetProperty("ships", out listingsArray) && listingsArray.ValueKind == JsonValueKind.Array);
        if (!found)
            return Array.Empty<ShipyardListingEntry>();

        var entries = new List<ShipyardListingEntry>();
        foreach (var listing in listingsArray.EnumerateArray())
        {
            if (listing.ValueKind != JsonValueKind.Object)
                continue;

            string listingId =
                SpaceMoltJson.TryGetString(listing, "listing_id") ??
                SpaceMoltJson.TryGetString(listing, "id") ??
                "";
            string name = SpaceMoltJson.TryGetString(listing, "name") ?? "";
            string classId =
                SpaceMoltJson.TryGetString(listing, "class_id") ??
                SpaceMoltJson.TryGetString(listing, "ship_class") ??
                "";

            decimal? price =
                (listing.TryGetProperty("price", out var p0) && p0.ValueKind == JsonValueKind.Number && p0.TryGetDecimal(out var d0)) ? d0 :
                (listing.TryGetProperty("price_each", out var p1) && p1.ValueKind == JsonValueKind.Number && p1.TryGetDecimal(out var d1)) ? d1 :
                null;

            entries.Add(new ShipyardListingEntry
            {
                ListingId = listingId,
                Name = name,
                ClassId = classId,
                Price = price
            });
            if (entries.Count >= 12)
                break;
        }

        return entries.ToArray();
    }

    public static bool TryParseOwnedShips(JsonElement response, out OwnedShipInfo[] ships)
    {
        ships = Array.Empty<OwnedShipInfo>();

        if (response.ValueKind != JsonValueKind.Object)
            return false;

        JsonElement result = response;
        if (response.TryGetProperty("result", out var wrappedResult) &&
            wrappedResult.ValueKind == JsonValueKind.Object)
        {
            result = wrappedResult;
        }

        if (!result.TryGetProperty("ships", out var shipsArray) ||
            shipsArray.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        string activeShipId = SpaceMoltJson.TryGetString(result, "active_ship_id") ?? "";

        var parsed = new List<OwnedShipInfo>();
        foreach (var ship in shipsArray.EnumerateArray())
        {
            if (ship.ValueKind != JsonValueKind.Object)
                continue;

            string shipId = SpaceMoltJson.TryGetString(ship, "ship_id") ?? "";
            if (string.IsNullOrWhiteSpace(shipId))
                continue;

            parsed.Add(new OwnedShipInfo
            {
                ShipId = shipId,
                ClassId = SpaceMoltJson.TryGetString(ship, "class_id") ?? "",
                Location =
                    SpaceMoltJson.TryGetString(ship, "location") ??
                    SpaceMoltJson.TryGetString(ship, "location_base_id") ??
                    "",
                IsActive =
                    (ship.TryGetProperty("is_active", out var activeEl) && activeEl.ValueKind == JsonValueKind.True) ||
                    (!string.IsNullOrWhiteSpace(activeShipId) &&
                     string.Equals(shipId, activeShipId, StringComparison.Ordinal))
            });
        }

        ships = parsed.ToArray();
        return true;
    }

    public static MissionInfo ParseMissionInfo(JsonElement mission, bool isActiveMission)
    {
        string missionId = SpaceMoltJson.TryGetString(mission, "mission_id") ?? "";
        string templateId = SpaceMoltJson.TryGetString(mission, "template_id") ?? "";
        string progressText = SpaceMoltJson.TryGetString(mission, "progress_text") ?? "";
        string objectivesSummary = BuildObjectivesSummary(mission);
        string progressSummary = BuildProgressSummary(mission);

        if (string.IsNullOrWhiteSpace(progressText))
            progressText = BuildProgressText(progressSummary, objectivesSummary);

        bool completed = mission.TryGetProperty("completed", out var completedEl) && completedEl.ValueKind == JsonValueKind.True;
        if (!completed && isActiveMission && mission.TryGetProperty("objectives", out var objectivesEl) && objectivesEl.ValueKind == JsonValueKind.Array)
        {
            int objectiveCount = 0;
            int completedObjectives = 0;
            foreach (var objective in objectivesEl.EnumerateArray())
            {
                objectiveCount++;
                if (objective.ValueKind == JsonValueKind.Object &&
                    objective.TryGetProperty("completed", out var objectiveCompleted) &&
                    objectiveCompleted.ValueKind == JsonValueKind.True)
                {
                    completedObjectives++;
                }
            }

            completed = objectiveCount > 0 && completedObjectives == objectiveCount;
        }

        var info = new MissionInfo
        {
            Id = isActiveMission
                ? missionId
                : (!string.IsNullOrWhiteSpace(templateId) ? templateId : missionId),
            MissionId = missionId,
            TemplateId = templateId,
            Title = SpaceMoltJson.TryGetString(mission, "title") ?? "",
            Type = SpaceMoltJson.TryGetString(mission, "type") ?? "",
            Description = SpaceMoltJson.TryGetString(mission, "description") ?? "",
            ProgressText = progressText,
            Completed = completed,
            Difficulty = SpaceMoltJson.TryGetInt(mission, "difficulty"),
            ExpiresInTicks = SpaceMoltJson.TryGetInt(mission, "expires_in_ticks"),
            AcceptedAt = SpaceMoltJson.TryGetString(mission, "accepted_at") ?? "",
            IssuingBase = SpaceMoltJson.TryGetString(mission, "issuing_base") ?? "",
            IssuingBaseId = SpaceMoltJson.TryGetString(mission, "issuing_base_id") ?? "",
            GiverName = "",
            GiverTitle = "",
            Repeatable = SpaceMoltJson.TryGetBool(mission, "repeatable"),
            FactionId = SpaceMoltJson.TryGetString(mission, "faction_id") ?? "",
            FactionName = SpaceMoltJson.TryGetString(mission, "faction_name") ?? "",
            ChainNext = SpaceMoltJson.TryGetString(mission, "chain_next") ?? "",
            ObjectivesSummary = objectivesSummary,
            ProgressSummary = progressSummary,
            RequirementsSummary = BuildRequirementsSummary(mission),
            RewardsSummary = BuildRewardsSummary(mission)
        };

        if (mission.TryGetProperty("giver", out var giver) && giver.ValueKind == JsonValueKind.Object)
        {
            info.GiverName = SpaceMoltJson.TryGetString(giver, "name") ?? "";
            info.GiverTitle = SpaceMoltJson.TryGetString(giver, "title") ?? "";
        }

        return info;
    }

    private static string BuildObjectivesSummary(JsonElement mission)
    {
        if (!mission.TryGetProperty("objectives", out var objectivesEl) || objectivesEl.ValueKind != JsonValueKind.Array)
            return "";

        var parts = new List<string>();
        foreach (var objective in objectivesEl.EnumerateArray())
        {
            if (objective.ValueKind != JsonValueKind.Object)
                continue;

            string type = SpaceMoltJson.TryGetString(objective, "type") ?? "objective";
            string description = SpaceMoltJson.TryGetString(objective, "description") ?? "";
            int? current = SpaceMoltJson.TryGetInt(objective, "current");
            int? required = SpaceMoltJson.TryGetInt(objective, "required");
            bool isDone = objective.TryGetProperty("completed", out var completedEl) && completedEl.ValueKind == JsonValueKind.True;

            var piece = string.IsNullOrWhiteSpace(description) ? type : $"{type}: {description}";
            if (current.HasValue || required.HasValue)
                piece += $" ({current?.ToString() ?? "-"} / {required?.ToString() ?? "-"})";
            if (isDone)
                piece += " [done]";

            parts.Add(piece);
        }

        return string.Join("; ", parts);
    }

    private static string BuildProgressSummary(JsonElement mission)
    {
        if (!mission.TryGetProperty("progress", out var progressEl) || progressEl.ValueKind != JsonValueKind.Object)
            return "";

        var parts = new List<string>();
        AppendIntPart(parts, progressEl, "items_delivered");
        AppendIntPart(parts, progressEl, "items_mined");
        AppendIntPart(parts, progressEl, "kills_achieved");
        AppendIntPart(parts, progressEl, "systems_visited");
        AppendPercentPart(parts, progressEl, "percent_complete");
        return string.Join(", ", parts);
    }

    private static string BuildRequirementsSummary(JsonElement mission)
    {
        if (!mission.TryGetProperty("requirements", out var requirementsEl) || requirementsEl.ValueKind != JsonValueKind.Object)
            return "";

        var parts = new List<string>();
        AppendStringPart(parts, requirementsEl, "deliver_item_id");
        AppendStringPart(parts, requirementsEl, "deliver_item_name");
        AppendIntPart(parts, requirementsEl, "deliver_quantity");
        AppendStringPart(parts, requirementsEl, "deliver_to_base_id");
        AppendStringPart(parts, requirementsEl, "deliver_to_base_name");
        AppendIntPart(parts, requirementsEl, "kill_count");
        AppendStringPart(parts, requirementsEl, "mine_item_id");
        AppendStringPart(parts, requirementsEl, "mine_item_name");
        AppendIntPart(parts, requirementsEl, "mine_quantity");
        AppendStringPart(parts, requirementsEl, "target_player_id");
        AppendIntPart(parts, requirementsEl, "visit_system_count");
        return string.Join(", ", parts);
    }

    private static string BuildRewardsSummary(JsonElement mission)
    {
        if (!mission.TryGetProperty("rewards", out var rewardsEl) || rewardsEl.ValueKind != JsonValueKind.Object)
            return "";

        var parts = new List<string>();
        AppendIntPart(parts, rewardsEl, "credits");
        AppendIntPart(parts, rewardsEl, "reputation");
        AppendMapPart(parts, rewardsEl, "items");
        AppendMapPart(parts, rewardsEl, "skill_xp");
        return string.Join(", ", parts);
    }

    private static string BuildProgressText(string progressSummary, string objectivesSummary)
    {
        if (!string.IsNullOrWhiteSpace(progressSummary))
            return progressSummary;

        if (!string.IsNullOrWhiteSpace(objectivesSummary))
            return objectivesSummary;

        return "";
    }

    private static void AppendStringPart(List<string> parts, JsonElement obj, string key)
    {
        string? value = SpaceMoltJson.TryGetString(obj, key);
        if (!string.IsNullOrWhiteSpace(value))
            parts.Add($"{key}={value}");
    }

    private static void AppendIntPart(List<string> parts, JsonElement obj, string key)
    {
        int? value = SpaceMoltJson.TryGetInt(obj, key);
        if (value.HasValue)
            parts.Add($"{key}={value.Value}");
    }

    private static void AppendPercentPart(List<string> parts, JsonElement obj, string key)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(key, out var prop) || prop.ValueKind != JsonValueKind.Number)
            return;

        if (prop.TryGetDouble(out var value))
            parts.Add($"{key}={(value * 100.0):0.#}%");
    }

    private static void AppendMapPart(List<string> parts, JsonElement obj, string key)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(key, out var map) || map.ValueKind != JsonValueKind.Object)
            return;

        var entries = new List<string>();
        foreach (var property in map.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var value))
                continue;

            entries.Add($"{property.Name}:{value}");
        }

        if (entries.Count > 0)
            parts.Add($"{key}={{ {string.Join(", ", entries)} }}");
    }
}
