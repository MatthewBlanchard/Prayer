using System;
using System.Collections.Generic;
using System.Linq;
using Contracts = Prayer.Contracts;

internal static class RuntimeStateContractMapper
{
    public static Contracts.RuntimeGameStateDto Map(GameState source)
    {
        return new Contracts.RuntimeGameStateDto
        {
            System = source.System,
            CurrentPOI = Map(source.CurrentPOI ?? new POIInfo()),
            POIs = source.POIs.Select(Map).ToArray(),
            Systems = source.Systems.ToArray(),
            Galaxy = Map(source.Galaxy),
            StorageCredits = source.StorageCredits,
            StorageItems = MapItemStackDictionary(source.StorageItems),
            EconomyDeals = source.EconomyDeals.Select(Map).ToArray(),
            OwnBuyOrders = source.OwnBuyOrders.Select(Map).ToArray(),
            OwnSellOrders = source.OwnSellOrders.Select(Map).ToArray(),
            Ship = Map(source.Ship),
            Credits = source.Credits,
            Docked = source.Docked,
            ShipyardShowroom = source.ShipyardShowroom.Select(Map).ToArray(),
            ShipyardListings = source.ShipyardListings.Select(Map).ToArray(),
            ShipCatalogue = Map(source.ShipCatalogue),
            OwnedShips = source.OwnedShips.Select(Map).ToArray(),
            AvailableRecipes = source.AvailableRecipes.Select(Map).ToArray(),
            Skills = new Dictionary<string, int>(source.Skills ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase),
            ActiveMissions = source.ActiveMissions.Select(Map).ToArray(),
            AvailableMissions = source.AvailableMissions.Select(Map).ToArray(),
            Notifications = source.Notifications.Select(Map).ToArray(),
            ChatMessages = source.ChatMessages.Select(Map).ToArray(),
            CurrentMarket = source.CurrentMarket == null ? null : Map(source.CurrentMarket)
        };
    }

    private static Contracts.RuntimePlayerShipDto Map(PlayerShip source)
    {
        return new Contracts.RuntimePlayerShipDto
        {
            Name = source.Name,
            ClassId = source.ClassId,
            Armor = source.Armor,
            Speed = source.Speed,
            CpuUsed = source.CpuUsed,
            CpuCapacity = source.CpuCapacity,
            PowerUsed = source.PowerUsed,
            PowerCapacity = source.PowerCapacity,
            ModuleCount = source.ModuleCount,
            Fuel = source.Fuel,
            MaxFuel = source.MaxFuel,
            Hull = source.Hull,
            MaxHull = source.MaxHull,
            Shield = source.Shield,
            MaxShield = source.MaxShield,
            CargoUsed = source.CargoUsed,
            CargoCapacity = source.CargoCapacity,
            Cargo = MapItemStackDictionary(source.Cargo)
        };
    }

    private static Contracts.RuntimeGalaxyStateDto Map(GalaxyState source)
    {
        return new Contracts.RuntimeGalaxyStateDto
        {
            Map = Map(source.Map),
            Market = Map(source.Market),
            Catalog = Map(source.Catalog),
            Resources = Map(source.Resources),
            Exploration = Map(source.Exploration),
            UpdatedAtUtc = source.UpdatedAtUtc
        };
    }

    private static Contracts.RuntimeGalaxyExplorationDto Map(GalaxyExplorationState source)
    {
        return new Contracts.RuntimeGalaxyExplorationDto
        {
            ExploredSystems = (source.ExploredSystems ?? new HashSet<string>(StringComparer.Ordinal))
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .OrderBy(v => v, StringComparer.Ordinal)
                .ToArray(),
            VisitedPois = (source.VisitedPois ?? new HashSet<string>(StringComparer.Ordinal))
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .OrderBy(v => v, StringComparer.Ordinal)
                .ToArray(),
            SurveyedSystems = (source.SurveyedSystems ?? new HashSet<string>(StringComparer.Ordinal))
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .OrderBy(v => v, StringComparer.Ordinal)
                .ToArray(),
            MiningCheckedPoisByResource = MapResourceIndex(source.MiningCheckedPoisByResource),
            MiningExploredSystemsByResource = MapResourceIndex(source.MiningExploredSystemsByResource)
        };
    }

    private static Contracts.RuntimeGalaxyResourcesDto Map(GalaxyResources source)
    {
        return new Contracts.RuntimeGalaxyResourcesDto
        {
            SystemsByResource = source.SystemsByResource.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToArray(),
                StringComparer.Ordinal),
            PoisByResource = source.PoisByResource.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToArray(),
                StringComparer.Ordinal)
        };
    }

    private static Dictionary<string, string[]> MapResourceIndex(Dictionary<string, string[]>? source)
    {
        source ??= new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        return source.ToDictionary(
            pair => pair.Key,
            pair => (pair.Value ?? Array.Empty<string>())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(v => v, StringComparer.Ordinal)
                .ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static Contracts.RuntimeGalaxyMarketDto Map(GalaxyMarket source)
    {
        return new Contracts.RuntimeGalaxyMarketDto
        {
            MarketsByStation = source.MarketsByStation.ToDictionary(
                pair => pair.Key,
                pair => Map(pair.Value),
                StringComparer.Ordinal),
            GlobalMedianBuyPrices = new Dictionary<string, decimal>(source.GlobalMedianBuyPrices, StringComparer.Ordinal),
            GlobalMedianSellPrices = new Dictionary<string, decimal>(source.GlobalMedianSellPrices, StringComparer.Ordinal),
            GlobalWeightedMidPrices = new Dictionary<string, decimal>(source.GlobalWeightedMidPrices, StringComparer.Ordinal)
        };
    }

    private static Contracts.RuntimeGalaxyCatalogDto Map(GalaxyCatalog source)
    {
        return new Contracts.RuntimeGalaxyCatalogDto
        {
            ItemsById = source.ItemsById.ToDictionary(
                pair => pair.Key,
                pair => Map(pair.Value),
                StringComparer.Ordinal),
            ShipsById = source.ShipsById.ToDictionary(
                pair => pair.Key,
                pair => Map(pair.Value),
                StringComparer.Ordinal)
        };
    }

    private static Contracts.RuntimeItemCatalogueEntryDto Map(ItemCatalogueEntry source)
    {
        return new Contracts.RuntimeItemCatalogueEntryDto
        {
            Id = source.Id,
            Name = source.Name,
            ClassId = source.ClassId,
            Class = source.Class,
            Category = source.Category,
            Type = source.Type,
            Tier = source.Tier,
            Scale = source.Scale,
            Hull = source.Hull,
            BaseHull = source.BaseHull,
            Shield = source.Shield,
            BaseShield = source.BaseShield,
            Cargo = source.Cargo,
            CargoCapacity = source.CargoCapacity,
            Speed = source.Speed,
            BaseSpeed = source.BaseSpeed,
            Price = source.Price,
            MaterialsById = source.MaterialsById == null
                ? null
                : new Dictionary<string, int>(source.MaterialsById, StringComparer.Ordinal),
            Ingredients = (source.Ingredients ?? Array.Empty<RecipeIngredientEntry>())
                .Select(Map)
                .ToArray(),
            Inputs = (source.Inputs ?? Array.Empty<RecipeIngredientEntry>())
                .Select(Map)
                .ToArray(),
            Outputs = (source.Outputs ?? Array.Empty<RecipeIngredientEntry>())
                .Select(Map)
                .ToArray(),
            RequiredSkills = source.RequiredSkills == null
                ? null
                : new Dictionary<string, int>(source.RequiredSkills, StringComparer.Ordinal)
        };
    }

    private static Contracts.RuntimeShipCatalogueEntryDto Map(ShipCatalogueEntry source)
    {
        return new Contracts.RuntimeShipCatalogueEntryDto
        {
            Id = source.Id,
            Name = source.Name,
            ClassId = source.ClassId,
            Class = source.Class,
            Category = source.Category,
            Type = source.Type,
            Tier = source.Tier,
            Scale = source.Scale,
            Hull = source.Hull,
            BaseHull = source.BaseHull,
            Shield = source.Shield,
            BaseShield = source.BaseShield,
            Cargo = source.Cargo,
            CargoCapacity = source.CargoCapacity,
            Speed = source.Speed,
            BaseSpeed = source.BaseSpeed,
            Price = source.Price,
            MaterialsById = source.MaterialsById == null
                ? null
                : new Dictionary<string, int>(source.MaterialsById, StringComparer.Ordinal),
            Ingredients = (source.Ingredients ?? Array.Empty<RecipeIngredientEntry>())
                .Select(Map)
                .ToArray(),
            Inputs = (source.Inputs ?? Array.Empty<RecipeIngredientEntry>())
                .Select(Map)
                .ToArray(),
            Outputs = (source.Outputs ?? Array.Empty<RecipeIngredientEntry>())
                .Select(Map)
                .ToArray(),
            RequiredSkills = source.RequiredSkills == null
                ? null
                : new Dictionary<string, int>(source.RequiredSkills, StringComparer.Ordinal)
        };
    }

    private static Contracts.RuntimeGalaxyMapSnapshotDto Map(GalaxyMapSnapshot source)
    {
        return new Contracts.RuntimeGalaxyMapSnapshotDto
        {
            Systems = source.Systems.Select(Map).ToList(),
            KnownPois = source.KnownPois.Select(Map).ToList()
        };
    }

    private static Contracts.RuntimeGalaxySystemInfoDto Map(GalaxySystemInfo source)
    {
        return new Contracts.RuntimeGalaxySystemInfoDto
        {
            Id = source.Id,
            Empire = source.Empire,
            X = source.X,
            Y = source.Y,
            Connections = source.Connections.ToList(),
            Pois = source.Pois.Select(Map).ToList()
        };
    }

    private static Contracts.RuntimeGalaxyPoiInfoDto Map(GalaxyPoiInfo source)
    {
        return new Contracts.RuntimeGalaxyPoiInfoDto
        {
            Id = source.Id,
            X = source.X,
            Y = source.Y
        };
    }

    private static Contracts.RuntimeGalaxyKnownPoiInfoDto Map(GalaxyKnownPoiInfo source)
    {
        return new Contracts.RuntimeGalaxyKnownPoiInfoDto
        {
            Id = source.Id,
            SystemId = source.SystemId,
            Name = source.Name,
            Type = source.Type,
            X = source.X,
            Y = source.Y,
            HasBase = source.HasBase,
            BaseId = source.BaseId,
            BaseName = source.BaseName,
            LastSeenUtc = source.LastSeenUtc
        };
    }

    private static Contracts.RuntimeMarketStateDto Map(MarketState source)
    {
        return new Contracts.RuntimeMarketStateDto
        {
            StationId = source.StationId,
            SellOrders = source.SellOrders.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Select(Map).ToList(),
                StringComparer.Ordinal),
            BuyOrders = source.BuyOrders.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Select(Map).ToList(),
                StringComparer.Ordinal)
        };
    }

    private static Contracts.RuntimeMarketOrderDto Map(MarketOrder source)
    {
        return new Contracts.RuntimeMarketOrderDto
        {
            ItemId = source.ItemId,
            PriceEach = source.PriceEach,
            Quantity = source.Quantity
        };
    }

    private static Contracts.RuntimeEconomyDealDto Map(EconomyDeal source)
    {
        return new Contracts.RuntimeEconomyDealDto
        {
            ItemId = source.ItemId,
            BuyStationId = source.BuyStationId,
            BuyPrice = source.BuyPrice,
            SellStationId = source.SellStationId,
            SellPrice = source.SellPrice,
            ProfitPerUnit = source.ProfitPerUnit
        };
    }

    private static Contracts.RuntimeOpenOrderInfoDto Map(OpenOrderInfo source)
    {
        return new Contracts.RuntimeOpenOrderInfoDto
        {
            OrderId = source.OrderId,
            ItemId = source.ItemId,
            PriceEach = source.PriceEach,
            Quantity = source.Quantity
        };
    }

    private static Contracts.RuntimeOwnedShipInfoDto Map(OwnedShipInfo source)
    {
        return new Contracts.RuntimeOwnedShipInfoDto
        {
            ShipId = source.ShipId,
            ClassId = source.ClassId,
            Location = source.Location,
            IsActive = source.IsActive
        };
    }

    private static Contracts.RuntimeShipyardShowroomEntryDto Map(ShipyardShowroomEntry source)
    {
        return new Contracts.RuntimeShipyardShowroomEntryDto
        {
            ShipClassId = source.ShipClassId,
            ShipId = source.ShipId,
            Name = source.Name,
            Category = source.Category,
            Tier = source.Tier,
            Scale = source.Scale,
            Hull = source.Hull,
            Shield = source.Shield,
            Cargo = source.Cargo,
            Speed = source.Speed,
            Price = source.Price
        };
    }

    private static Contracts.RuntimeShipyardListingEntryDto Map(ShipyardListingEntry source)
    {
        return new Contracts.RuntimeShipyardListingEntryDto
        {
            ListingId = source.ListingId,
            Name = source.Name,
            ClassId = source.ClassId,
            Price = source.Price
        };
    }

    private static Dictionary<string, Contracts.RuntimeItemStackDto> MapItemStackDictionary(Dictionary<string, ItemStack> source)
    {
        return source.ToDictionary(
            pair => pair.Key,
            pair => Map(pair.Value),
            StringComparer.Ordinal);
    }

    private static Contracts.RuntimeItemStackDto Map(ItemStack source)
    {
        return new Contracts.RuntimeItemStackDto
        {
            ItemId = source.ItemId,
            Quantity = source.Quantity
        };
    }

    private static Contracts.RuntimePoiInfoDto Map(POIInfo source)
    {
        return new Contracts.RuntimePoiInfoDto
        {
            Id = source.Id,
            SystemId = source.SystemId,
            Name = source.Name,
            Type = source.Type,
            Description = source.Description,
            Hidden = source.Hidden,
            X = source.X,
            Y = source.Y,
            HasBase = source.HasBase,
            BaseId = source.BaseId,
            BaseName = source.BaseName,
            Online = source.Online,
            Resources = source.Resources.Select(Map).ToArray()
        };
    }

    private static Contracts.RuntimePoiResourceInfoDto Map(PoiResourceInfo source)
    {
        return new Contracts.RuntimePoiResourceInfoDto
        {
            ResourceId = source.ResourceId,
            Name = source.Name,
            RichnessText = source.RichnessText,
            Richness = source.Richness,
            Remaining = source.Remaining,
            RemainingDisplay = source.RemainingDisplay
        };
    }

    private static Contracts.RuntimeMissionInfoDto Map(MissionInfo source)
    {
        return new Contracts.RuntimeMissionInfoDto
        {
            Id = source.Id,
            MissionId = source.MissionId,
            TemplateId = source.TemplateId,
            Title = source.Title,
            Type = source.Type,
            Description = source.Description,
            ProgressText = source.ProgressText,
            Completed = source.Completed,
            Difficulty = source.Difficulty,
            ExpiresInTicks = source.ExpiresInTicks,
            AcceptedAt = source.AcceptedAt,
            IssuingBase = source.IssuingBase,
            IssuingBaseId = source.IssuingBaseId,
            GiverName = source.GiverName,
            GiverTitle = source.GiverTitle,
            Repeatable = source.Repeatable,
            FactionId = source.FactionId,
            FactionName = source.FactionName,
            ChainNext = source.ChainNext,
            ObjectivesSummary = source.ObjectivesSummary,
            ProgressSummary = source.ProgressSummary,
            RequirementsSummary = source.RequirementsSummary,
            RewardsSummary = source.RewardsSummary
        };
    }

    private static Contracts.RuntimeGameNotificationDto Map(GameNotification source)
    {
        return new Contracts.RuntimeGameNotificationDto
        {
            Type = source.Type,
            Summary = source.Summary,
            PayloadJson = source.PayloadJson
        };
    }

    private static Contracts.RuntimeGameChatMessageDto Map(GameChatMessage source)
    {
        return new Contracts.RuntimeGameChatMessageDto
        {
            MessageId = source.MessageId,
            Channel = source.Channel,
            Sender = source.Sender,
            Content = source.Content,
            SeenTick = source.SeenTick
        };
    }

    private static Contracts.RuntimeCatalogueDto Map(Catalogue source)
    {
        return new Contracts.RuntimeCatalogueDto
        {
            Type = source.Type,
            Category = source.Category,
            Id = source.Id,
            Page = source.Page,
            PageSize = source.PageSize,
            TotalPages = source.TotalPages,
            TotalItems = source.TotalItems,
            Total = source.Total,
            Message = source.Message,
            Items = source.Items.Select(Map).ToArray(),
            Entries = source.Entries.Select(Map).ToArray(),
            Ships = source.Ships.Select(Map).ToArray()
        };
    }

    private static Contracts.RuntimeCatalogueEntryDto Map(CatalogueEntry source)
    {
        return new Contracts.RuntimeCatalogueEntryDto
        {
            Id = source.Id,
            Name = source.Name,
            ClassId = source.ClassId,
            Class = source.Class,
            Category = source.Category,
            Type = source.Type,
            Tier = source.Tier,
            Scale = source.Scale,
            Hull = source.Hull,
            BaseHull = source.BaseHull,
            Shield = source.Shield,
            BaseShield = source.BaseShield,
            Cargo = source.Cargo,
            CargoCapacity = source.CargoCapacity,
            Speed = source.Speed,
            BaseSpeed = source.BaseSpeed,
            Price = source.Price,
            MaterialsById = source.MaterialsById == null
                ? null
                : new Dictionary<string, int>(source.MaterialsById, StringComparer.Ordinal),
            Ingredients = (source.Ingredients ?? Array.Empty<RecipeIngredientEntry>())
                .Select(Map)
                .ToArray(),
            Inputs = (source.Inputs ?? Array.Empty<RecipeIngredientEntry>())
                .Select(Map)
                .ToArray(),
            Outputs = (source.Outputs ?? Array.Empty<RecipeIngredientEntry>())
                .Select(Map)
                .ToArray(),
            RequiredSkills = source.RequiredSkills == null
                ? null
                : new Dictionary<string, int>(source.RequiredSkills, StringComparer.Ordinal)
        };
    }

    private static Contracts.RuntimeRecipeIngredientEntryDto Map(RecipeIngredientEntry source)
    {
        return new Contracts.RuntimeRecipeIngredientEntryDto
        {
            ItemId = source.ItemId,
            Item = source.Item,
            Id = source.Id,
            Name = source.Name,
            Quantity = source.Quantity,
            Amount = source.Amount,
            Count = source.Count
        };
    }
}
