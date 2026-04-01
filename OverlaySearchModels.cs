using System.Collections.Generic;
using System.Text.Json;

namespace StarCitizenOverLay
{
    internal sealed class OverlaySearchResponse
    {
        public string Query { get; set; } = string.Empty;
        public int Total { get; set; }
        public List<OverlaySearchItem> Results { get; set; } = [];
    }

    internal sealed class OverlaySearchItem
    {
        public string CategoryKey { get; set; } = string.Empty;
        public string CategoryLabel { get; set; } = "未知分类";
        public int CategoryPriority { get; set; }
        public string SourceType { get; set; } = string.Empty;
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? NameChs { get; set; }
        public string? Size { get; set; }
        public string? Type { get; set; }
        public string? Rank { get; set; }
        public int? Rarity { get; set; }
        public int Score { get; set; }
        public OverlayItemFlags? Flags { get; set; }
    }

    internal sealed class OverlayItemFlags
    {
        public bool ApiPrice { get; set; }
        public bool ManualPrice { get; set; }
        public bool Pledge { get; set; }
        public bool Subscriber { get; set; }
        public bool Concierge { get; set; }
        public bool Limited { get; set; }
        public bool EventAward { get; set; }
        public bool Lucky { get; set; }
        public bool Banu { get; set; }
    }

    internal sealed class OverlayItemDetailResponse
    {
        public string CategoryKey { get; set; } = string.Empty;
        public string CategoryLabel { get; set; } = "未知分类";
        public int CategoryPriority { get; set; }
        public string SourceType { get; set; } = string.Empty;
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? NameChs { get; set; }
        public string? ManufacturerName { get; set; }
        public string? ManufacturerNameChs { get; set; }
        public OverlayItemFlags? Flags { get; set; }
        public List<OverlayDetailSection> Sections { get; set; } = [];
        public OverlayDetailExtras Extras { get; set; } = new();
    }

    internal sealed class OverlayDetailSection
    {
        public string Key { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public List<OverlayDetailField> Items { get; set; } = [];
    }

    internal sealed class OverlayDetailField
    {
        public string Key { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public JsonElement Value { get; set; }
        public string? Unit { get; set; }
    }

    internal sealed class OverlayDetailExtras
    {
        public string? AccessAdvice { get; set; }
        public string? Update { get; set; }
        public JsonElement? Armorset { get; set; }
        public JsonElement? Blueprint { get; set; }
        public JsonElement? MiningSources { get; set; }
    }

    internal sealed class OverlayItemPriceResponse
    {
        public string CategoryKey { get; set; } = string.Empty;
        public string CategoryLabel { get; set; } = string.Empty;
        public int CategoryPriority { get; set; }
        public string SourceType { get; set; } = string.Empty;
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? NameChs { get; set; }
        public OverlayPriceSummary Summary { get; set; } = new();
        public List<OverlayPriceEntry> Buy { get; set; } = [];
        public List<OverlayPriceEntry> Sell { get; set; } = [];
        public List<OverlayPriceEntry> Rent { get; set; } = [];
        public OverlayPriceExtras Extras { get; set; } = new();
    }

    internal sealed class OverlayPriceSummary
    {
        public int BuyCount { get; set; }
        public int SellCount { get; set; }
        public int RentCount { get; set; }
        public int? MinBuyPrice { get; set; }
        public int? MaxBuyPrice { get; set; }
        public int? MinSellPrice { get; set; }
        public int? MaxSellPrice { get; set; }
        public int? MinRentPrice { get; set; }
        public int? MaxRentPrice { get; set; }
    }

    internal sealed class OverlayPriceEntry
    {
        public int? Price { get; set; }
        public string? Location { get; set; }
        public int? TerminalId { get; set; }
        public string? TerminalName { get; set; }
        public string? GameVersion { get; set; }
        public int? DurationDays { get; set; }
    }

    internal sealed class OverlayPriceExtras
    {
        public long? UpdatedAt { get; set; }
        public string? ClaimTime { get; set; }
        public string? ExpediteTime { get; set; }
        public int? ExpediteFee { get; set; }
        public string? RentAt { get; set; }
    }

    internal sealed class OverlayMissionDetailResponse
    {
        public string Id { get; set; } = string.Empty;
        public string? Name { get; set; }
        public string? NameChs { get; set; }
        public string? Description { get; set; }
        public string? DescriptionChs { get; set; }
        public string? Category { get; set; }
        public string? CategoryChs { get; set; }
        public string? MissionType { get; set; }
        public string? MissionTypeChs { get; set; }
        public string? SourceType { get; set; }
        public string? GameVersion { get; set; }
        public OverlayMissionFlags? Flags { get; set; }
        public List<OverlayDetailSection> Sections { get; set; } = [];
        public OverlayMissionExtras Extras { get; set; } = new();
    }

    internal sealed class OverlayMissionFlags
    {
        public bool CanBeShared { get; set; }
        public bool Illegal { get; set; }
    }

    internal sealed class OverlayMissionExtras
    {
        public List<string> Systems { get; set; } = [];
        public JsonElement? Prerequisites { get; set; }
        public JsonElement? RequiredIntros { get; set; }
        public JsonElement? UnlockMissions { get; set; }
        public JsonElement? ReceiveItems { get; set; }
        public JsonElement? RequiredItems { get; set; }
        public JsonElement? BlueprintRewards { get; set; }
        public JsonElement? Reputation { get; set; }
        public JsonElement? Combat { get; set; }
        public decimal? ScripAmount { get; set; }
        public JsonElement? FailReputationAmounts { get; set; }
    }
}
