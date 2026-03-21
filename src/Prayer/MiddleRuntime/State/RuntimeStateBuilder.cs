using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

public sealed class RuntimeStateBuilder
{
    public GameState BuildBaseState(
        string currentSystem,
        POIInfo currentPoi,
        POIInfo[] pois,
        string[] jumpTargets,
        Dictionary<string, ItemStack> cargo,
        JsonElement player,
        JsonElement ship,
        bool docked)
    {
        var skills = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (player.TryGetProperty("skills", out var skillsEl) && skillsEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in skillsEl.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(property.Name))
                    continue;
                if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var level))
                    continue;

                skills[property.Name] = level;
            }
        }

        return new GameState
        {
            System = currentSystem,
            CurrentPOI = currentPoi,
            POIs = pois,
            Systems = jumpTargets,
            Ship = new PlayerShip
            {
                Cargo = cargo,
                Name = ship.TryGetProperty("name", out var shipNameEl) && shipNameEl.ValueKind == JsonValueKind.String
                    ? shipNameEl.GetString() ?? ""
                    : "",
                ClassId = ship.TryGetProperty("class_id", out var shipClassEl) && shipClassEl.ValueKind == JsonValueKind.String
                    ? shipClassEl.GetString() ?? ""
                    : "",
                Armor = ship.TryGetProperty("armor", out var armorEl) && armorEl.ValueKind == JsonValueKind.Number
                    ? armorEl.GetInt32()
                    : 0,
                Speed = ship.TryGetProperty("speed", out var speedEl) && speedEl.ValueKind == JsonValueKind.Number
                    ? speedEl.GetInt32()
                    : 0,
                CpuUsed = ship.TryGetProperty("cpu_used", out var cpuUsedEl) && cpuUsedEl.ValueKind == JsonValueKind.Number
                    ? cpuUsedEl.GetInt32()
                    : 0,
                CpuCapacity = ship.TryGetProperty("cpu_capacity", out var cpuCapEl) && cpuCapEl.ValueKind == JsonValueKind.Number
                    ? cpuCapEl.GetInt32()
                    : 0,
                PowerUsed = ship.TryGetProperty("power_used", out var powerUsedEl) && powerUsedEl.ValueKind == JsonValueKind.Number
                    ? powerUsedEl.GetInt32()
                    : 0,
                PowerCapacity = ship.TryGetProperty("power_capacity", out var powerCapEl) && powerCapEl.ValueKind == JsonValueKind.Number
                    ? powerCapEl.GetInt32()
                    : 0,
                ModuleCount = ship.TryGetProperty("modules", out var modulesEl) && modulesEl.ValueKind == JsonValueKind.Array
                    ? modulesEl.GetArrayLength()
                    : 0,
                InstalledModules = ship.TryGetProperty("modules", out modulesEl) && modulesEl.ValueKind == JsonValueKind.Array
                    ? modulesEl
                        .EnumerateArray()
                        .Select(module =>
                        {
                            if (module.ValueKind == JsonValueKind.String)
                                return module.GetString() ?? string.Empty;
                            if (module.ValueKind != JsonValueKind.Object)
                                return string.Empty;

                            return (module.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                                    ? nameEl.GetString()
                                    : null)
                                ?? (module.TryGetProperty("module_id", out var moduleIdEl) && moduleIdEl.ValueKind == JsonValueKind.String
                                    ? moduleIdEl.GetString()
                                    : null)
                                ?? (module.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                                    ? idEl.GetString()
                                    : null)
                                ?? string.Empty;
                        })
                        .Where(v => !string.IsNullOrWhiteSpace(v))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray()
                    : Array.Empty<string>(),
                Fuel = ship.GetProperty("fuel").GetInt32(),
                MaxFuel = ship.GetProperty("max_fuel").GetInt32(),
                Hull = ship.GetProperty("hull").GetInt32(),
                MaxHull = ship.GetProperty("max_hull").GetInt32(),
                Shield = ship.GetProperty("shield").GetInt32(),
                MaxShield = ship.GetProperty("max_shield").GetInt32(),
                CargoUsed = ship.GetProperty("cargo_used").GetInt32(),
                CargoCapacity = ship.GetProperty("cargo_capacity").GetInt32()
            },
            Credits = player.GetProperty("credits").GetInt32(),
            Docked = docked,
            HomeBase = player.TryGetProperty("home_base", out var homeBaseEl) && homeBaseEl.ValueKind == JsonValueKind.String
                ? homeBaseEl.GetString() ?? ""
                : "",
            Skills = skills,
            Notifications = Array.Empty<GameNotification>(),
            ChatMessages = Array.Empty<GameChatMessage>()
        };
    }

    public void ApplyDockedStationState(
        GameState state,
        int storageCredits,
        Dictionary<string, ItemStack> storageItems,
        EconomyDeal[] economyDeals,
        OpenOrderInfo[] ownBuyOrders,
        OpenOrderInfo[] ownSellOrders,
        ShipyardShowroomEntry[] shipyardShowroom,
        ShipyardListingEntry[] shipyardListings,
        Catalogue shipCatalogue)
    {
        state.StorageCredits = storageCredits;
        state.StorageItems = storageItems;
        state.EconomyDeals = economyDeals;
        state.OwnBuyOrders = ownBuyOrders;
        state.OwnSellOrders = ownSellOrders;
        state.ShipyardShowroom = shipyardShowroom;
        state.ShipyardListings = shipyardListings;
        state.ShipCatalogue = shipCatalogue;
    }

    public void ApplyUndockedDefaults(GameState state)
    {
        state.StorageCredits = 0;
        state.StorageItems = new Dictionary<string, ItemStack>();
        state.EconomyDeals = Array.Empty<EconomyDeal>();
        state.OwnBuyOrders = Array.Empty<OpenOrderInfo>();
        state.OwnSellOrders = Array.Empty<OpenOrderInfo>();
        state.ShipyardShowroom = Array.Empty<ShipyardShowroomEntry>();
        state.ShipyardListings = Array.Empty<ShipyardListingEntry>();
        state.ShipCatalogue = new Catalogue();
    }

    public void ApplyGlobalState(
        GameState state,
        GalaxyState galaxy,
        OwnedShipInfo[] ownedShips,
        MissionInfo[] activeMissions,
        MissionInfo[] availableMissions,
        GameNotification[] notifications,
        GameChatMessage[] chatMessages)
    {
        state.Galaxy = galaxy;
        state.OwnedShips = ownedShips;
        state.ActiveMissions = activeMissions;
        state.AvailableMissions = availableMissions;
        state.Notifications = notifications;
        state.ChatMessages = chatMessages;
    }
}
