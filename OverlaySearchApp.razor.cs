using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace StarCitizenOverLay
{
    public partial class OverlaySearchApp : ComponentBase
    {
        private string _query = "VK-00";
        private bool _isSearching;
        private bool _hasSearched;
        private readonly List<OverlaySearchItem> _results = [];
        private OverlaySearchItem? _selectedItem;

        private string _detailTitle = "物品搜索终端";
        private string _detailSubtitle = "输入关键词开始搜索。";
        private string _detailStatus = string.Empty;
        private string _detailUpdatedAt = string.Empty;

        private readonly List<string> _accessRows = [];
        private readonly List<PriceRowVm> _priceRows = [];
        private readonly List<string> _miningRows = [];

        private BlueprintHeaderVm? _blueprintHeader;
        private readonly List<BlueprintMaterialVm> _blueprintMaterials = [];
        private readonly List<BlueprintBaseStatSourceVm> _blueprintBaseStats = [];
        private readonly List<BlueprintModifierSlotVm> _blueprintModifierSlots = [];
        private readonly List<MissionSourceVm> _missionSources = [];

        private string _missionDetailTitle = string.Empty;
        private string _missionDetailSubtitle = string.Empty;
        private string _missionDetailDescription = string.Empty;
        private string _missionDetailStatus = string.Empty;
        private readonly List<MissionRowVm> _missionDetailRows = [];
        private string? _selectedMissionSourceId;

        private int _selectionRequestVersion;
        private int _missionRequestVersion;

        protected override async Task OnInitializedAsync()
        {
            if (ApiService.HasConfiguration)
            {
                await SearchAsync();
            }
        }

        private async Task SearchAsync()
        {
            if (_isSearching)
            {
                return;
            }

            _hasSearched = true;
            var query = _query.Trim();
            if (string.IsNullOrWhiteSpace(query))
            {
                _results.Clear();
                ResetDetail();
                return;
            }

            _isSearching = true;
            _results.Clear();
            ResetDetail();

            try
            {
                var response = await ApiService.SearchAsync(query);
                _results.AddRange(response.Results
                    .OrderBy(item => item.CategoryPriority)
                    .ThenByDescending(item => item.Score)
                    .ThenBy(item => GetPrimaryName(item), StringComparer.CurrentCultureIgnoreCase));

                if (_results.Count == 0)
                {
                    _detailTitle = "没有找到结果";
                    _detailSubtitle = $"没有匹配“{query}”的物品。";
                    return;
                }

                await SelectItemAsync(_results[0]);
            }
            catch (Exception ex)
            {
                _detailTitle = "搜索失败";
                _detailSubtitle = ex.Message;
                _detailStatus = ex.Message;
            }
            finally
            {
                _isSearching = false;
            }
        }

        private async Task HandleSearchInputKeyDown(KeyboardEventArgs args)
        {
            if (args.Key == "Enter")
            {
                await SearchAsync();
            }
        }

        private async Task SelectItemAsync(OverlaySearchItem item)
        {
            var requestVersion = ++_selectionRequestVersion;

            _selectedItem = item;
            _detailTitle = GetPrimaryName(item);
            _detailSubtitle = BuildItemSubtitle(item);
            _detailStatus = string.Empty;
            _detailUpdatedAt = string.Empty;

            _accessRows.Clear();
            _priceRows.Clear();
            _miningRows.Clear();
            ClearBlueprintState();
            ClearMissionDetail();

            await Task.WhenAll(
                LoadDetailAsync(item, requestVersion),
                LoadPriceAsync(item, requestVersion));
        }

        private void ResetDetail()
        {
            _selectedItem = null;
            _detailTitle = "物品搜索终端";
            _detailSubtitle = ApiService.HasConfiguration
                ? "输入关键词开始搜索。"
                : "未检测到 API_BASE_URL，请检查 .env 配置。";
            _detailStatus = string.Empty;
            _detailUpdatedAt = string.Empty;
            _accessRows.Clear();
            _priceRows.Clear();
            _miningRows.Clear();
            ClearBlueprintState();
            ClearMissionDetail();
        }

        private async Task LoadDetailAsync(OverlaySearchItem item, int requestVersion)
        {
            try
            {
                var detail = await ApiService.GetDetailAsync(item.CategoryKey, item.Id);
                if (!IsSelectionRequestCurrent(item, requestVersion))
                {
                    return;
                }

                _detailTitle = FirstNonEmpty(detail.NameChs, detail.Name, GetPrimaryName(item));
                _detailSubtitle = BuildDetailSubtitle(detail, item);
                _detailStatus = string.Empty;
                _detailUpdatedAt = detail.Extras.Update?.Trim() ?? string.Empty;

                _accessRows.Clear();
                _accessRows.AddRange(BuildAccessRows(detail.Extras.AccessAdvice));

                ApplyBlueprint(detail.Extras.Blueprint);

                _miningRows.Clear();
                _miningRows.AddRange(BuildMiningRows(detail.Extras.MiningSources));

                if (_missionSources.Count > 0)
                {
                    await LoadMissionDetailAsync(_missionSources[0]);
                }
            }
            catch (Exception ex)
            {
                if (!IsSelectionRequestCurrent(item, requestVersion))
                {
                    return;
                }

                _detailStatus = $"详情加载失败：{ex.Message}";
                _accessRows.Clear();
                _miningRows.Clear();
                ClearBlueprintState();
                ClearMissionDetail();
            }
        }

        private async Task LoadPriceAsync(OverlaySearchItem item, int requestVersion)
        {
            try
            {
                var price = await ApiService.GetPriceAsync(item.CategoryKey, item.Id);
                if (!IsSelectionRequestCurrent(item, requestVersion))
                {
                    return;
                }

                _priceRows.Clear();
                AddPriceRows("buy", price.Buy);
                AddPriceRows("sell", price.Sell);

                if (string.IsNullOrWhiteSpace(_detailUpdatedAt))
                {
                    _detailUpdatedAt = FormatUnixTimestamp(price.Extras.UpdatedAt);
                }
            }
            catch (Exception ex)
            {
                if (!IsSelectionRequestCurrent(item, requestVersion))
                {
                    return;
                }

                if (string.IsNullOrWhiteSpace(_detailStatus))
                {
                    _detailStatus = $"价格加载失败：{ex.Message}";
                }

                _priceRows.Clear();
            }
        }

        private async Task LoadMissionDetailAsync(MissionSourceVm mission)
        {
            if (string.IsNullOrWhiteSpace(mission.Id))
            {
                return;
            }

            var requestVersion = ++_missionRequestVersion;

            _selectedMissionSourceId = mission.Id;
            _missionDetailTitle = mission.Title;
            _missionDetailSubtitle = mission.SecondaryText ?? string.Empty;
            _missionDetailDescription = string.Empty;
            _missionDetailStatus = "正在加载任务详情...";
            _missionDetailRows.Clear();

            try
            {
                var detail = await ApiService.GetMissionDetailAsync(mission.Id);
                if (requestVersion != _missionRequestVersion || mission.Id != _selectedMissionSourceId)
                {
                    return;
                }

                _missionDetailTitle = FirstNonEmpty(detail.NameChs, detail.Name, mission.Title);
                _missionDetailSubtitle = BuildMissionSubtitle(detail);
                _missionDetailDescription = FirstNonEmpty(detail.DescriptionChs, detail.Description);
                _missionDetailStatus = string.Empty;

                _missionDetailRows.Clear();
                _missionDetailRows.AddRange(BuildMissionRows(detail));
            }
            catch (Exception ex)
            {
                if (requestVersion != _missionRequestVersion || mission.Id != _selectedMissionSourceId)
                {
                    return;
                }

                _missionDetailStatus = $"任务详情加载失败：{ex.Message}";
                _missionDetailRows.Clear();
            }
        }

        private void ClearBlueprintState()
        {
            _blueprintHeader = null;
            _blueprintMaterials.Clear();
            _blueprintBaseStats.Clear();
            _blueprintModifierSlots.Clear();
            _missionSources.Clear();
        }

        private void ClearMissionDetail()
        {
            _selectedMissionSourceId = null;
            _missionDetailTitle = string.Empty;
            _missionDetailSubtitle = string.Empty;
            _missionDetailDescription = string.Empty;
            _missionDetailStatus = string.Empty;
            _missionDetailRows.Clear();
            _missionRequestVersion++;
        }

        private void OnBlueprintQualityChanged(string slotKey, ChangeEventArgs args)
        {
            if (string.IsNullOrWhiteSpace(slotKey))
            {
                return;
            }

            var slot = _blueprintModifierSlots.FirstOrDefault(item => item.Key == slotKey);
            if (slot is null)
            {
                return;
            }

            if (!int.TryParse(args.Value?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return;
            }

            slot.CurrentQuality = Math.Clamp(parsed, slot.MinQuality, 1000);
        }

        private bool IsSelectionRequestCurrent(OverlaySearchItem item, int requestVersion)
            => requestVersion == _selectionRequestVersion
               && _selectedItem?.CategoryKey == item.CategoryKey
               && _selectedItem?.Id == item.Id;

        private void ApplyBlueprint(JsonElement? blueprint)
        {
            ClearBlueprintState();

            if (blueprint is null ||
                blueprint.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ||
                blueprint.Value.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var value = blueprint.Value;
            var title = FirstJsonString(value, "nameChs", "name");
            var subtitle = FirstJsonString(value, "categoryName");

            if (!string.IsNullOrWhiteSpace(title))
            {
                _blueprintHeader = new BlueprintHeaderVm(title, subtitle, BuildBlueprintTags(value));
            }

            if (value.TryGetProperty("materials", out var materials) && materials.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var material in materials.EnumerateArray().Take(24))
                {
                    var row = BuildBlueprintMaterialRow(material, index++);
                    if (row is not null)
                    {
                        _blueprintMaterials.Add(row);
                    }
                }
            }

            if (value.TryGetProperty("baseStats", out var baseStats) && baseStats.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var stat in baseStats.EnumerateArray())
                {
                    var source = BuildBlueprintBaseStatSource(stat, index++);
                    if (source is not null)
                    {
                        _blueprintBaseStats.Add(source);
                    }
                }
            }

            if (value.TryGetProperty("modifiers", out var modifiers) && modifiers.ValueKind == JsonValueKind.Array)
            {
                var slotIndex = 0;
                foreach (var slot in modifiers.EnumerateArray())
                {
                    var parsedSlot = BuildBlueprintModifierSlot(slot, slotIndex++);
                    if (parsedSlot is not null)
                    {
                        _blueprintModifierSlots.Add(parsedSlot);
                    }
                }
            }

            if (value.TryGetProperty("missionSources", out var missionSources) && missionSources.ValueKind == JsonValueKind.Array)
            {
                var missionIndex = 0;
                foreach (var mission in missionSources.EnumerateArray())
                {
                    var source = BuildMissionSource(mission, missionIndex++);
                    if (source is not null)
                    {
                        _missionSources.Add(source);
                    }
                }
            }
        }

        private static List<BlueprintTagVm> BuildBlueprintTags(JsonElement blueprint)
        {
            var tags = new List<BlueprintTagVm>();

            if (TryGetBoolean(blueprint, "isReward", out var isReward) && isReward)
            {
                tags.Add(new BlueprintTagVm("任务奖励", BuildTagStyle("#51232C", "#C4546A", "#FFD4DD")));
            }

            if (TryGetInt(blueprint, "craftTimeSec", out var craftTimeSec) && craftTimeSec > 0)
            {
                tags.Add(new BlueprintTagVm($"制作 {FormatDuration(craftTimeSec)}", BuildTagStyle("#1D425A", "#3392C2", "#D4F2FF")));
            }

            var version = FirstJsonString(blueprint, "gameVersion");
            if (!string.IsNullOrWhiteSpace(version))
            {
                tags.Add(new BlueprintTagVm(version, BuildTagStyle("#2A3350", "#6878B4", "#E2E9FF")));
            }

            return tags;
        }

        private static BlueprintMaterialVm? BuildBlueprintMaterialRow(JsonElement material, int index)
        {
            var name = FirstJsonString(material, "nameChs", "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var parts = new List<string> { name };

            if (TryGetDecimal(material, "quantity", out var quantity) && quantity > 0)
            {
                parts.Add($"x{FormatNumber(quantity)}");
            }

            if (TryGetDecimal(material, "scu", out var scu) && scu > 0)
            {
                parts.Add($"{FormatNumber(scu)} SCU");
            }

            return new BlueprintMaterialVm($"material:{index}:{name}", string.Join(" · ", parts));
        }

        private static BlueprintBaseStatSourceVm? BuildBlueprintBaseStatSource(JsonElement stat, int index)
        {
            var displayName = FirstJsonString(stat, "propertyChs", "property");
            if (string.IsNullOrWhiteSpace(displayName))
            {
                return null;
            }

            var propertyKey = FirstJsonString(stat, "property");
            var valueText = FirstJsonValue(stat, "value");
            if (string.IsNullOrWhiteSpace(valueText))
            {
                return null;
            }

            var numericValue = stat.TryGetProperty("value", out var valueElement) &&
                               TryGetDecimalValue(valueElement, out var decimalValue)
                ? (decimal?)decimalValue
                : null;

            return new BlueprintBaseStatSourceVm(
                $"blueprint-stat:{index}:{propertyKey}:{displayName}",
                string.IsNullOrWhiteSpace(propertyKey) ? displayName : propertyKey,
                displayName,
                valueText,
                numericValue);
        }

        private static BlueprintModifierSlotVm? BuildBlueprintModifierSlot(JsonElement slot, int index)
        {
            var title = FirstJsonString(slot, "slotName", "slotNameEn", "slotKey");
            if (string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            var key = FirstJsonString(slot, "slotKey", "slotNameEn", "slotName");
            if (string.IsNullOrWhiteSpace(key))
            {
                key = $"slot-{index}";
            }

            var minQuality = TryGetInt(slot, "minQuality", out var parsedMinQuality)
                ? Math.Clamp(parsedMinQuality, 0, 1000)
                : 0;

            var requiredCount = TryGetInt(slot, "requiredCount", out var parsedRequiredCount)
                ? parsedRequiredCount
                : 1;

            var modifierRows = new List<BlueprintModifierValueVm>();
            if (slot.TryGetProperty("modifiers", out var modifiers) && modifiers.ValueKind == JsonValueKind.Array)
            {
                var modifierIndex = 0;
                foreach (var modifier in modifiers.EnumerateArray())
                {
                    var displayName = FirstJsonString(modifier, "propertyChs", "property");
                    if (string.IsNullOrWhiteSpace(displayName))
                    {
                        continue;
                    }

                    modifierRows.Add(new BlueprintModifierValueVm(
                        $"slot-modifier:{key}:{modifierIndex++}:{displayName}",
                        FirstJsonString(modifier, "property") is { Length: > 0 } propertyKey ? propertyKey : displayName,
                        displayName,
                        TryGetDecimal(modifier, "startQuality", out var startQuality) ? startQuality : null,
                        TryGetDecimal(modifier, "endQuality", out var endQuality) ? endQuality : null,
                        TryGetDecimal(modifier, "startValue", out var startValue) ? startValue : null,
                        TryGetDecimal(modifier, "endValue", out var endValue) ? endValue : null));
                }
            }

            return new BlueprintModifierSlotVm(
                key,
                title,
                requiredCount,
                minQuality,
                1000,
                modifierRows);
        }

        private static MissionSourceVm? BuildMissionSource(JsonElement mission, int index)
        {
            var missionId = FirstJsonString(mission, "id");
            var missionName = FirstJsonString(mission, "nameChs", "name");
            if (string.IsNullOrWhiteSpace(missionId) || string.IsNullOrWhiteSpace(missionName))
            {
                return null;
            }

            var secondaryParts = new List<string>();
            var missionType = FirstJsonString(mission, "missionTypeChs", "missionType");
            var factionName = FirstJsonString(mission, "factionName");
            if (!string.IsNullOrWhiteSpace(missionType))
            {
                secondaryParts.Add(missionType);
            }

            if (!string.IsNullOrWhiteSpace(factionName))
            {
                secondaryParts.Add(factionName);
            }

            if (TryGetInt(mission, "rewardUec", out var rewardUec) && rewardUec > 0)
            {
                secondaryParts.Add($"{rewardUec:N0} aUEC");
            }

            return new MissionSourceVm(
                $"mission-source:{index}:{missionId}",
                missionId,
                missionName,
                secondaryParts.Count == 0 ? null : string.Join(" · ", secondaryParts));
        }

        private void AddPriceRows(string sideKey, IEnumerable<OverlayPriceEntry> entries)
        {
            foreach (var entry in entries.Take(60))
            {
                var location = BuildPreferredPriceLocation(entry);
                _priceRows.Add(new PriceRowVm(
                    $"{_selectedItem?.CategoryKey}:{_selectedItem?.Id}:{sideKey}:{_priceRows.Count}",
                    sideKey,
                    location,
                    BuildPriceRowSecondary(entry, location),
                    FormatPrice(entry.Price)));
            }
        }

        private static string GetPrimaryName(OverlaySearchItem item)
            => string.IsNullOrWhiteSpace(item.NameChs) ? item.Name : item.NameChs!;

        private static string BuildItemSubtitle(OverlaySearchItem item)
        {
            if (!string.IsNullOrWhiteSpace(item.Name) &&
                !string.Equals(item.Name, item.NameChs, StringComparison.OrdinalIgnoreCase))
            {
                return item.Name;
            }

            return "点击结果查看详情。";
        }

        private static string BuildDetailSubtitle(OverlayItemDetailResponse detail, OverlaySearchItem item)
        {
            var lines = new List<string>();

            if (!string.IsNullOrWhiteSpace(detail.Name) &&
                !string.Equals(detail.Name, detail.NameChs, StringComparison.OrdinalIgnoreCase))
            {
                lines.Add(detail.Name);
            }

            var manufacturer = BuildManufacturerText(detail);
            if (!string.IsNullOrWhiteSpace(manufacturer))
            {
                lines.Add($"制造商：{manufacturer}");
            }

            if (lines.Count == 0)
            {
                lines.Add(BuildItemSubtitle(item));
            }

            return string.Join(Environment.NewLine, lines);
        }

        private static string BuildManufacturerText(OverlayItemDetailResponse detail)
        {
            if (!string.IsNullOrWhiteSpace(detail.ManufacturerNameChs))
            {
                return detail.ManufacturerNameChs!;
            }

            return detail.ManufacturerName ?? string.Empty;
        }

        private static IEnumerable<ResultTagVm> BuildResultTags(OverlaySearchItem item)
        {
            var tags = new List<ResultTagVm>();

            if (!string.IsNullOrWhiteSpace(item.CategoryLabel))
            {
                tags.Add(new ResultTagVm(item.CategoryLabel!, BuildCategoryTagStyle(item.CategoryKey)));
            }

            if (!string.IsNullOrWhiteSpace(item.Size))
            {
                tags.Add(new ResultTagVm(item.Size!, BuildTagStyle("#183D63", "#2B74C2", "#C7E5FF")));
            }
            else if (!string.IsNullOrWhiteSpace(item.Rank))
            {
                tags.Add(new ResultTagVm(item.Rank!, BuildTagStyle("#3A285F", "#7A59C8", "#E6D8FF")));
            }
            else if (!string.IsNullOrWhiteSpace(item.Type))
            {
                tags.Add(new ResultTagVm(item.Type!, BuildTagStyle("#4B3719", "#A7792E", "#FFE2B2")));
            }

            return tags
                .Where(tag => !string.IsNullOrWhiteSpace(tag.Text))
                .GroupBy(tag => tag.Text, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Take(2);
        }

        private static string BuildTagStyle(string background, string border, string foreground)
            => $"padding:1px 6px;border-radius:999px;background:{background};border:1px solid {border};color:{foreground};font-size:10px;font-weight:700;white-space:nowrap;";

        private static string BuildCategoryTagStyle(string categoryKey) => categoryKey switch
        {
            "ships" => BuildTagStyle("#173949", "#2E879F", "#B7F4FF"),
            "foods" => BuildTagStyle("#214A2B", "#3B9A4F", "#D0FFD7"),
            "drinks" => BuildTagStyle("#184654", "#2E92AA", "#C7F8FF"),
            "attachments" => BuildTagStyle("#59401B", "#C18A33", "#FFE3B3"),
            "miscellaneous" => BuildTagStyle("#33404D", "#72879A", "#E1EBF2"),
            "decorations" => BuildTagStyle("#552F4B", "#B66499", "#FFD6F0"),
            "flightblades" => BuildTagStyle("#204953", "#34A2B9", "#C8F7FF"),
            "liveries" => BuildTagStyle("#582F40", "#C16385", "#FFD6E3"),
            "cores" or "backpacks" or "arms" or "legs" or "helmets" or "undersuits"
                => BuildTagStyle("#263F5C", "#4D7CB4", "#D0E4FF"),
            "weapon_person" => BuildTagStyle("#5E2926", "#C95D4E", "#FFD5CF"),
            "weapon_ship" => BuildTagStyle("#5A341F", "#BF744C", "#FFD8C2"),
            "missiles" or "bombs" => BuildTagStyle("#5D2323", "#D04F4F", "#FFD7D7"),
            "shields" => BuildTagStyle("#223B60", "#5583D7", "#D8E7FF"),
            "quantums" => BuildTagStyle("#33255C", "#8261D6", "#E9DEFF"),
            "powerplants" => BuildTagStyle("#5A3818", "#C3862E", "#FFE0A8"),
            "coolers" => BuildTagStyle("#1E4956", "#36A1B9", "#C8F9FF"),
            "radars" => BuildTagStyle("#1F415D", "#4A8AC1", "#D2ECFF"),
            "tractorbeams" => BuildTagStyle("#235247", "#46AB90", "#D5FFF0"),
            "jumpmodules" => BuildTagStyle("#37245A", "#8B65DE", "#EBDFFF"),
            "lifesupports" => BuildTagStyle("#1D4B3D", "#379C74", "#D1FFE8"),
            "mininglasers" => BuildTagStyle("#5D431B", "#CC9439", "#FFE7B1"),
            "miningmodules" => BuildTagStyle("#5C3517", "#C9772A", "#FFD8AD"),
            "mininggadgets" => BuildTagStyle("#513516", "#AF7C2A", "#FFE0B3"),
            "commodities" => BuildTagStyle("#5A4B18", "#D1A83A", "#FFEDAE"),
            _ => BuildTagStyle("#19445E", "#2E89BC", "#ABE3FF")
        };

        private static IEnumerable<string> BuildAccessRows(string? accessAdvice)
        {
            if (string.IsNullOrWhiteSpace(accessAdvice))
            {
                return [];
            }

            return accessAdvice
                .Replace("\r", "\n")
                .Split(['\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(row => !string.IsNullOrWhiteSpace(row))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(24)
                .ToList();
        }

        private static List<string> BuildMiningRows(JsonElement? miningSources)
        {
            if (miningSources is null ||
                miningSources.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ||
                miningSources.Value.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return miningSources.Value.EnumerateArray()
                .Select(FormatMiningSource)
                .Where(row => !string.IsNullOrWhiteSpace(row))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(32)
                .ToList();
        }

        private static string FormatMiningSource(JsonElement source)
        {
            if (source.ValueKind != JsonValueKind.Object)
            {
                return string.Empty;
            }

            var location = FirstJsonString(source, "locationNameChs", "locationName");
            var systemName = FirstJsonString(source, "systemName");
            var percentText = FirstJsonValue(source, "percent");

            if (!string.IsNullOrWhiteSpace(percentText) &&
                decimal.TryParse(percentText, NumberStyles.Any, CultureInfo.InvariantCulture, out var percent))
            {
                percentText = $"{percent:0.##}%";
            }

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(location)) parts.Add(location);
            if (!string.IsNullOrWhiteSpace(systemName)) parts.Add(systemName);
            if (!string.IsNullOrWhiteSpace(percentText)) parts.Add(percentText);

            return string.Join(" - ", parts);
        }

        private static List<MissionRowVm> BuildMissionRows(OverlayMissionDetailResponse detail)
        {
            var rows = new List<MissionRowVm>();
            var index = 0;

            MissionRowVm Section(string title)
                => new($"mission-section:{index++}", MissionRowKind.Section, title);

            MissionRowVm Text(string text, string? secondary = null)
                => new($"mission-text:{index++}", MissionRowKind.Text, text, secondary);

            foreach (var section in detail.Sections.Where(section => section.Items.Count > 0))
            {
                rows.Add(Section(section.Title));

                foreach (var item in section.Items)
                {
                    var value = FormatDetailFieldValue(item);
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        rows.Add(Text(item.Label, value));
                    }
                }
            }

            if (detail.Extras.Systems.Count > 0)
            {
                rows.Add(Section("星系"));
                rows.Add(Text(string.Join(" / ", detail.Extras.Systems)));
            }

            if (detail.Extras.ReceiveItems is { } receiveItems && receiveItems.ValueKind == JsonValueKind.Array)
            {
                AddMissionItemRows(rows, receiveItems, "奖励物品", ref index);
            }

            if (detail.Extras.RequiredItems is { } requiredItems && requiredItems.ValueKind == JsonValueKind.Array)
            {
                AddMissionItemRows(rows, requiredItems, "提交需求", ref index);
            }

            if (detail.Extras.BlueprintRewards is { } blueprintRewards && blueprintRewards.ValueKind == JsonValueKind.Object)
            {
                AddMissionBlueprintRewardRows(rows, blueprintRewards, ref index);
            }

            if (detail.Extras.Reputation is { } reputation && reputation.ValueKind == JsonValueKind.Object)
            {
                AddMissionReputationRows(rows, reputation, ref index);
            }

            return rows;
        }

        private static void AddMissionBlueprintRewardRows(List<MissionRowVm> rows, JsonElement blueprintRewards, ref int index)
        {
            var buffer = new List<MissionRowVm>();

            if (blueprintRewards.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.Object)
            {
                var parts = new List<string>();

                if (TryGetInt(summary, "poolItemCount", out var poolItemCount) && poolItemCount > 0)
                {
                    parts.Add($"池内 {poolItemCount} 项");
                }

                if (TryGetDecimal(summary, "poolChancePercent", out var poolChance) && poolChance > 0)
                {
                    parts.Add($"触发 {FormatNumber(poolChance)}%");
                }

                if (parts.Count > 0)
                {
                    buffer.Add(new MissionRowVm($"mission-text:{index++}", MissionRowKind.Text, string.Join(" · ", parts)));
                }
            }

            if (blueprintRewards.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray().Take(12))
                {
                    var name = FirstJsonString(item, "nameChs", "name");
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    var secondary = TryGetDecimal(item, "chancePercent", out var chance)
                        ? $"{FormatNumber(chance)}%"
                        : null;

                    buffer.Add(new MissionRowVm($"mission-text:{index++}", MissionRowKind.Text, name, secondary));
                }
            }

            if (buffer.Count == 0)
            {
                return;
            }

            rows.Add(new MissionRowVm($"mission-section:{index++}", MissionRowKind.Section, "蓝图奖励"));
            rows.AddRange(buffer);
        }

        private static void AddMissionItemRows(List<MissionRowVm> rows, JsonElement items, string title, ref int index)
        {
            var entries = new List<MissionRowVm>();
            foreach (var item in items.EnumerateArray().Take(16))
            {
                var name = FirstJsonString(item, "nameChs", "name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var secondary = TryGetDecimal(item, "quantity", out var quantity) && quantity > 0
                    ? $"x{FormatNumber(quantity)}"
                    : null;

                entries.Add(new MissionRowVm($"mission-text:{index++}", MissionRowKind.Text, name, secondary));
            }

            if (entries.Count == 0)
            {
                return;
            }

            rows.Add(new MissionRowVm($"mission-section:{index++}", MissionRowKind.Section, title));
            rows.AddRange(entries);
        }

        private static void AddMissionReputationRows(List<MissionRowVm> rows, JsonElement reputation, ref int index)
        {
            var parts = new List<string>();
            var faction = FirstJsonString(reputation, "factionName");
            var scope = FirstJsonString(reputation, "scopeName");

            if (!string.IsNullOrWhiteSpace(faction))
            {
                parts.Add(faction);
            }

            if (!string.IsNullOrWhiteSpace(scope))
            {
                parts.Add(scope);
            }

            if (TryGetDecimal(reputation, "repPerMission", out var repPerMission) && repPerMission > 0)
            {
                parts.Add($"+{FormatNumber(repPerMission)} / 次");
            }

            if (parts.Count == 0)
            {
                return;
            }

            rows.Add(new MissionRowVm($"mission-section:{index++}", MissionRowKind.Section, "声望"));
            rows.Add(new MissionRowVm($"mission-text:{index++}", MissionRowKind.Text, string.Join(" · ", parts)));
        }

        private static string BuildMissionSubtitle(OverlayMissionDetailResponse detail)
        {
            var parts = new List<string>();
            var missionType = FirstNonEmpty(detail.MissionTypeChs, detail.MissionType);
            var category = FirstNonEmpty(detail.CategoryChs, detail.Category);

            if (!string.IsNullOrWhiteSpace(missionType))
            {
                parts.Add(missionType);
            }

            if (!string.IsNullOrWhiteSpace(category))
            {
                parts.Add(category);
            }

            if (detail.Extras.Systems.Count > 0)
            {
                parts.Add(string.Join(" / ", detail.Extras.Systems));
            }

            return string.Join(" · ", parts);
        }

        private static string FormatDetailFieldValue(OverlayDetailField field)
        {
            var value = FormatJsonElement(field.Value);
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return string.IsNullOrWhiteSpace(field.Unit) ? value : $"{value} {field.Unit}";
        }

        private static string FormatJsonElement(JsonElement element) => element.ValueKind switch
        {
            JsonValueKind.String => element.GetString()?.Trim() ?? string.Empty,
            JsonValueKind.Number => element.ToString(),
            JsonValueKind.True => "是",
            JsonValueKind.False => "否",
            JsonValueKind.Array => string.Join(" / ", element.EnumerateArray()
                .Select(FormatJsonElement)
                .Where(text => !string.IsNullOrWhiteSpace(text))),
            _ => string.Empty
        };

        private static bool TryGetBoolean(JsonElement element, string name, out bool value)
        {
            value = false;
            if (!element.TryGetProperty(name, out var property))
            {
                return false;
            }

            if (property.ValueKind == JsonValueKind.True)
            {
                value = true;
                return true;
            }

            if (property.ValueKind == JsonValueKind.False)
            {
                value = false;
                return true;
            }

            if (property.ValueKind == JsonValueKind.String && bool.TryParse(property.GetString(), out value))
            {
                return true;
            }

            return false;
        }

        private static bool TryGetInt(JsonElement element, string name, out int value)
        {
            value = 0;
            if (!element.TryGetProperty(name, out var property))
            {
                return false;
            }

            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out value))
            {
                return true;
            }

            if (property.ValueKind == JsonValueKind.String &&
                int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }

            return false;
        }

        private static bool TryGetDecimal(JsonElement element, string name, out decimal value)
        {
            value = 0;
            if (!element.TryGetProperty(name, out var property))
            {
                return false;
            }

            return TryGetDecimalValue(property, out value);
        }

        private static bool TryGetDecimalValue(JsonElement element, out decimal value)
        {
            value = 0;

            if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out value))
            {
                return true;
            }

            if (element.ValueKind == JsonValueKind.String &&
                decimal.TryParse(element.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }

            return false;
        }

        private static string FirstJsonString(JsonElement element, params string[] names)
        {
            foreach (var name in names)
            {
                if (!element.TryGetProperty(name, out var property))
                {
                    continue;
                }

                if (property.ValueKind == JsonValueKind.String)
                {
                    var text = property.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text;
                    }
                }
            }

            return string.Empty;
        }

        private static string FirstJsonValue(JsonElement element, params string[] names)
        {
            foreach (var name in names)
            {
                if (!element.TryGetProperty(name, out var property))
                {
                    continue;
                }

                var text = FormatJsonElement(property);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }

            return string.Empty;
        }

        private static string BuildPreferredPriceLocation(OverlayPriceEntry entry)
        {
            var location = entry.Location?.Trim() ?? string.Empty;
            var terminalName = entry.TerminalName?.Trim() ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(location) && ContainsCjk(location))
            {
                return location;
            }

            if (!string.IsNullOrWhiteSpace(terminalName) && ContainsCjk(terminalName))
            {
                return terminalName;
            }

            if (!string.IsNullOrWhiteSpace(location))
            {
                return location;
            }

            if (!string.IsNullOrWhiteSpace(terminalName))
            {
                return terminalName;
            }

            return "未知地点";
        }

        private static string BuildPriceRowSecondary(OverlayPriceEntry entry, string primaryLocation)
        {
            var parts = new List<string>();
            var terminalName = entry.TerminalName?.Trim();

            if (!string.IsNullOrWhiteSpace(terminalName) &&
                !string.Equals(terminalName, primaryLocation, StringComparison.OrdinalIgnoreCase) &&
                !ContainsCjk(primaryLocation))
            {
                parts.Add(terminalName);
            }

            if (!string.IsNullOrWhiteSpace(entry.GameVersion))
            {
                parts.Add(entry.GameVersion);
            }

            if (entry.DurationDays is > 0)
            {
                parts.Add($"{entry.DurationDays} 天");
            }

            return string.Join(" · ", parts);
        }

        private static string BuildPriceRowDisplay(PriceRowVm row)
            => row.Location;

        private static string FormatPrice(int? price)
            => price is null ? "--" : $"{price.Value:N0} aUEC";

        private static string FormatUnixTimestamp(long? unixTimestamp)
        {
            if (unixTimestamp is null || unixTimestamp <= 0)
            {
                return string.Empty;
            }

            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(unixTimestamp.Value)
                    .ToLocalTime()
                    .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string FormatDuration(int seconds)
        {
            if (seconds <= 0)
            {
                return string.Empty;
            }

            var time = TimeSpan.FromSeconds(seconds);
            if (time.TotalHours >= 1)
            {
                if (time.Minutes == 0)
                {
                    return $"{(int)time.TotalHours} 小时";
                }

                return $"{(int)time.TotalHours} 小时 {time.Minutes} 分";
            }

            if (time.TotalMinutes >= 1)
            {
                if (time.Seconds == 0)
                {
                    return $"{(int)time.TotalMinutes} 分";
                }

                return $"{(int)time.TotalMinutes} 分 {time.Seconds} 秒";
            }

            return $"{time.Seconds} 秒";
        }

        private static string FormatNumber(decimal value)
        {
            if (decimal.Truncate(value) == value)
            {
                return value.ToString("0", CultureInfo.InvariantCulture);
            }

            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string FirstNonEmpty(params string?[] values)
            => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

        private static bool ContainsCjk(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            return text.Any(ch => ch is >= '\u4E00' and <= '\u9FFF');
        }

        private string GetEmptyText()
        {
            if (!ApiService.HasConfiguration)
            {
                return "未检测到 API_BASE_URL，请检查 .env 配置。";
            }

            if (_isSearching)
            {
                return "正在搜索...";
            }

            if (!_hasSearched)
            {
                return "输入关键词开始搜索。";
            }

            return "没有找到结果。";
        }

        private string GetCompactResultCount()
            => _results.Count.ToString("000", CultureInfo.InvariantCulture);

        private string GetItemStyle(OverlaySearchItem item)
        {
            var selected = IsSelected(item);
            return selected
                ? "width:100%;height:36px;margin:0;padding:0 8px 0 10px;border-radius:8px;border:1px solid rgba(52,176,230,.92);box-shadow:inset 2px 0 0 #2ca9e1;background:rgba(29,46,63,.92);color:inherit;text-align:left;box-sizing:border-box;cursor:pointer;"
                : "width:100%;height:36px;margin:0;padding:0 8px 0 10px;border-radius:8px;border:1px solid rgba(67,104,129,.36);background:rgba(18,30,42,.84);color:inherit;text-align:left;box-sizing:border-box;cursor:pointer;";
        }

        private bool IsSelected(OverlaySearchItem item)
            => _selectedItem?.CategoryKey == item.CategoryKey && _selectedItem?.Id == item.Id;

        private bool ShouldShowDetailStatus()
            => !string.IsNullOrWhiteSpace(_detailStatus);

        private IReadOnlyList<PriceRowVm> GetBuyRows()
            => _priceRows.Where(row => row.SideKey == "buy").ToList();

        private IReadOnlyList<PriceRowVm> GetSellRows()
            => _priceRows.Where(row => row.SideKey == "sell").ToList();

        private IReadOnlyList<BlueprintStatVm> GetBlueprintStatRows()
        {
            var rows = new List<BlueprintStatVm>();

            foreach (var stat in _blueprintBaseStats)
            {
                var currentValue = stat.NumericValue;
                var factorApplied = false;

                if (currentValue.HasValue)
                {
                    foreach (var slot in _blueprintModifierSlots)
                    {
                        foreach (var modifier in slot.Modifiers.Where(modifier =>
                                     string.Equals(modifier.PropertyKey, stat.PropertyKey, StringComparison.OrdinalIgnoreCase)))
                        {
                            if (TryComputeModifierFactor(modifier, slot.CurrentQuality, out var factor))
                            {
                                currentValue *= factor;
                                factorApplied = true;
                            }
                        }
                    }
                }

                var valueText = factorApplied && currentValue.HasValue
                    ? $"{stat.DisplayText} → {FormatNumber(currentValue.Value)}"
                    : stat.DisplayText;

                rows.Add(new BlueprintStatVm(stat.Key, stat.DisplayName, valueText));
            }

            return rows;
        }

        private static bool TryComputeModifierFactor(BlueprintModifierValueVm modifier, int quality, out decimal factor)
        {
            factor = 1m;

            if (modifier.StartValue is null || modifier.EndValue is null)
            {
                return false;
            }

            var startValue = modifier.StartValue.Value;
            var endValue = modifier.EndValue.Value;

            if (modifier.StartQuality is null || modifier.EndQuality is null)
            {
                factor = endValue;
                return true;
            }

            var startQuality = modifier.StartQuality.Value;
            var endQuality = modifier.EndQuality.Value;

            if (endQuality == startQuality)
            {
                factor = endValue;
                return true;
            }

            var minQuality = Math.Min(startQuality, endQuality);
            var maxQuality = Math.Max(startQuality, endQuality);
            var clampedQuality = Math.Clamp(quality, (int)Math.Floor(minQuality), (int)Math.Ceiling(maxQuality));
            var t = (clampedQuality - startQuality) / (endQuality - startQuality);
            factor = startValue + (endValue - startValue) * t;
            return true;
        }

        private string BuildModifierPreviewText(BlueprintModifierSlotVm slot, BlueprintModifierValueVm modifier)
        {
            var parts = new List<string>();

            if (modifier.StartValue is not null && modifier.EndValue is not null)
            {
                parts.Add($"{FormatNumber(modifier.StartValue.Value)} → {FormatNumber(modifier.EndValue.Value)}");
            }

            if (modifier.StartQuality is not null && modifier.EndQuality is not null)
            {
                parts.Add($"质 {FormatNumber(modifier.StartQuality.Value)} - {FormatNumber(modifier.EndQuality.Value)}");
            }

            if (TryComputeModifierFactor(modifier, slot.CurrentQuality, out var factor))
            {
                parts.Add($"当前 x{FormatNumber(factor)}");
            }

            return string.Join(" · ", parts);
        }

        private string BuildSlotSubtitle(BlueprintModifierSlotVm slot)
        {
            var parts = new List<string>();

            if (slot.RequiredCount > 1)
            {
                parts.Add($"需求 x{slot.RequiredCount}");
            }

            if (slot.MinQuality > 0)
            {
                parts.Add($"最低质 {slot.MinQuality}");
            }

            return string.Join(" · ", parts);
        }

        private string BuildModifierTrackStyle(BlueprintModifierSlotVm slot)
        {
            var percent = Math.Clamp(slot.CurrentQuality, 0, 1000) / 10m;
            return $"width:{percent.ToString("0.##", CultureInfo.InvariantCulture)}%;";
        }

        private bool HasBuyColumn() => GetBuyRows().Count > 0;
        private bool HasSellColumn() => GetSellRows().Count > 0;
        private bool HasAccessColumn() => _accessRows.Count > 0;
        private bool HasBlueprintColumn() =>
            _blueprintHeader is not null ||
            _blueprintMaterials.Count > 0 ||
            _blueprintBaseStats.Count > 0 ||
            _blueprintModifierSlots.Count > 0;
        private bool HasMissionColumn() =>
            _missionSources.Count > 0 ||
            !string.IsNullOrWhiteSpace(_missionDetailTitle) ||
            !string.IsNullOrWhiteSpace(_missionDetailStatus);
        private bool HasMiningColumn() => _miningRows.Count > 0;

        private int GetVisibleColumnCount()
        {
            var count = 0;
            if (HasBuyColumn()) count++;
            if (HasSellColumn()) count++;
            if (HasAccessColumn()) count++;
            if (HasBlueprintColumn()) count++;
            if (HasMissionColumn()) count++;
            if (HasMiningColumn()) count++;
            return count;
        }

        private string BuildDataColumnsStyle()
        {
            var widths = new List<string>();
            if (HasBuyColumn()) widths.Add("minmax(0, 1fr)");
            if (HasSellColumn()) widths.Add("minmax(0, 1fr)");
            if (HasAccessColumn()) widths.Add("minmax(0, 0.90fr)");
            if (HasBlueprintColumn()) widths.Add("minmax(0, 1.28fr)");
            if (HasMissionColumn()) widths.Add("minmax(0, 1.15fr)");
            if (HasMiningColumn()) widths.Add("minmax(0, 1.00fr)");

            return $"height:100%;min-height:0;display:grid;grid-template-columns:{string.Join(" ", widths)};gap:8px;align-items:start;";
        }

        private static string BuildMissionSourceButtonStyle(bool selected)
            => selected
                ? "width:100%;padding:7px 8px;border:none;border-top:1px solid rgba(63,100,127,.16);background:rgba(20,43,63,.86);box-shadow:inset 2px 0 0 #2ca9e1;color:#fff;text-align:left;cursor:pointer;"
                : "width:100%;padding:7px 8px;border:none;border-top:1px solid rgba(63,100,127,.16);background:transparent;color:#fff;text-align:left;cursor:pointer;";

        private bool IsMissionSourceSelected(MissionSourceVm source)
            => !string.IsNullOrWhiteSpace(_selectedMissionSourceId) &&
               string.Equals(_selectedMissionSourceId, source.Id, StringComparison.Ordinal);

        private bool ShouldShowMissionDetailContent()
            => !string.IsNullOrWhiteSpace(_missionDetailTitle) ||
               !string.IsNullOrWhiteSpace(_missionDetailStatus) ||
               _missionDetailRows.Count > 0 ||
               !string.IsNullOrWhiteSpace(_missionDetailDescription);

        private string GetMissionDetailPlaceholder()
            => _missionSources.Count == 0
                ? "当前蓝图没有来源任务。"
                : "点击上方任务来源查看详情。";

        private sealed record PriceRowVm(string Key, string SideKey, string Location, string Detail, string PriceText);
        private sealed record ResultTagVm(string Text, string Style);
        private sealed record BlueprintTagVm(string Text, string Style);
        private sealed record BlueprintHeaderVm(string Title, string? Subtitle, List<BlueprintTagVm> Tags);
        private sealed record BlueprintMaterialVm(string Key, string Text);
        private sealed record BlueprintBaseStatSourceVm(string Key, string PropertyKey, string DisplayName, string DisplayText, decimal? NumericValue);
        private sealed record BlueprintStatVm(string Key, string Name, string ValueText);
        private sealed record BlueprintModifierValueVm(
            string Key,
            string PropertyKey,
            string DisplayName,
            decimal? StartQuality,
            decimal? EndQuality,
            decimal? StartValue,
            decimal? EndValue);

        private sealed class BlueprintModifierSlotVm
        {
            public BlueprintModifierSlotVm(string key, string title, int requiredCount, int minQuality, int currentQuality, List<BlueprintModifierValueVm> modifiers)
            {
                Key = key;
                Title = title;
                RequiredCount = requiredCount;
                MinQuality = minQuality;
                CurrentQuality = currentQuality;
                Modifiers = modifiers;
            }

            public string Key { get; }
            public string Title { get; }
            public int RequiredCount { get; }
            public int MinQuality { get; }
            public int CurrentQuality { get; set; }
            public List<BlueprintModifierValueVm> Modifiers { get; }
        }

        private sealed record MissionSourceVm(string Key, string Id, string Title, string? SecondaryText);
        private sealed record MissionRowVm(string Key, MissionRowKind Kind, string Text, string? SecondaryText = null);

        private enum MissionRowKind
        {
            Section,
            Text
        }
    }
}
