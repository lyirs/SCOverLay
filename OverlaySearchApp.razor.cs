using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace StarCitizenOverLay
{
    public partial class OverlaySearchApp : ComponentBase, IAsyncDisposable
    {
        [Inject]
        private IJSRuntime Js { get; set; } = default!;

        [Inject]
        private OverlayShellState ShellState { get; set; } = default!;

        private string _query = "P8";
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
        private readonly List<MaterialPreviewWindowVm> _materialPreviewWindows = [];

        private string _missionDetailTitle = string.Empty;
        private string _missionDetailSubtitle = string.Empty;
        private string _missionDetailDescription = string.Empty;
        private string _missionDetailStatus = string.Empty;
        private MissionDetailPanelVm? _missionDetailPanel;
        private readonly List<MissionRowVm> _missionDetailRows = [];
        private string? _selectedMissionSourceId;
        private int _nextMaterialPreviewOffset;
        private readonly FloatingWindowState _mainTerminalWindow = new(394, 0, 836, 12);
        private readonly FloatingWindowState _missionDetailWindow = new(704, 160, 760, 35);
        private string? _draggingWindowKey;
        private double _dragStartClientX;
        private double _dragStartClientY;
        private double _dragStartWindowLeft;
        private double _dragStartWindowTop;
        private int _nextWindowZIndex = 36;
        private DotNetObjectReference<OverlaySearchApp>? _dotNetReference;
        private bool _globalDragBridgeReady;

        private int _selectionRequestVersion;
        private int _missionRequestVersion;

        protected override async Task OnInitializedAsync()
        {
            ShellState.Changed += HandleShellStateChanged;

            if (ApiService.HasConfiguration)
            {
                await SearchAsync();
            }
        }

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (!firstRender)
            {
                return;
            }

            _dotNetReference = DotNetObjectReference.Create(this);
            await Js.InvokeVoidAsync("overlayFloatingWindows.register", _dotNetReference);
            _globalDragBridgeReady = true;
        }

        public async ValueTask DisposeAsync()
        {
            ShellState.Changed -= HandleShellStateChanged;

            if (_globalDragBridgeReady)
            {
                try
                {
                    await Js.InvokeVoidAsync("overlayFloatingWindows.unregister");
                }
                catch (JSDisconnectedException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
            }

            _dotNetReference?.Dispose();
        }

        private void HandleShellStateChanged()
        {
            _ = InvokeAsync(StateHasChanged);
        }

        private Task RefreshCombatLogFromShellAsync()
        {
            return ShellState.RequestCombatLogRefreshAsync();
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

        private async Task OpenMaterialPreviewWindowAsync(BlueprintMaterialVm material)
        {
            var existingWindow = _materialPreviewWindows.FirstOrDefault(window =>
                string.Equals(window.Key, material.Key, StringComparison.Ordinal));

            if (existingWindow is not null)
            {
                BringFloatingWindowToFront(existingWindow.Window);
                return;
            }

            var searchText = ExtractMaterialSearchText(material);
            if (string.IsNullOrWhiteSpace(searchText))
            {
                return;
            }

            var offset = _nextMaterialPreviewOffset++ % 6;
            var previewWindow = new MaterialPreviewWindowVm(
                material.Key,
                searchText,
                new FloatingWindowState(436 + (offset * 18), 146 + (offset * 18), 328, ++_nextWindowZIndex));

            _materialPreviewWindows.Add(previewWindow);
            await ShowBlueprintMaterialPreviewAsync(previewWindow, searchText);
        }

        private async Task ShowBlueprintMaterialPreviewAsync(MaterialPreviewWindowVm previewWindow, string searchText)
        {
            var requestVersion = ++previewWindow.RequestVersion;

            previewWindow.Title = searchText;
            previewWindow.Subtitle = "正在加载材料预览...";
            previewWindow.UpdatedAt = string.Empty;
            previewWindow.Status = string.Empty;
            previewWindow.AccessRows.Clear();
            previewWindow.MiningRows.Clear();
            previewWindow.PriceRows.Clear();

            try
            {
                var response = await ApiService.SearchAsync(searchText);
                if (!IsMaterialPreviewRequestCurrent(previewWindow, requestVersion))
                {
                    return;
                }

                var previewItem = PickMaterialPreviewItem(response.Results, searchText);
                if (previewItem is null)
                {
                    previewWindow.Title = searchText;
                    previewWindow.Subtitle = string.Empty;
                    previewWindow.Status = "没有找到可预览的材料条目。";
                    return;
                }

                previewWindow.Title = GetPrimaryName(previewItem);
                previewWindow.Subtitle = BuildMaterialPreviewSubtitle(previewItem);
                previewWindow.Status = string.Empty;

                var detailTask = ApiService.GetDetailAsync(previewItem.CategoryKey, previewItem.Id);
                var priceTask = ApiService.GetPriceAsync(previewItem.CategoryKey, previewItem.Id);
                await Task.WhenAll(detailTask, priceTask);

                if (!IsMaterialPreviewRequestCurrent(previewWindow, requestVersion))
                {
                    return;
                }

                var detail = await detailTask;
                var price = await priceTask;

                previewWindow.Title = FirstNonEmpty(detail.NameChs, detail.Name, GetPrimaryName(previewItem));
                previewWindow.Subtitle = BuildDetailSubtitle(detail, previewItem);
                previewWindow.UpdatedAt = !string.IsNullOrWhiteSpace(detail.Extras.Update)
                    ? detail.Extras.Update!.Trim()
                    : FormatUnixTimestamp(price.Extras.UpdatedAt);

                previewWindow.AccessRows.Clear();
                previewWindow.AccessRows.AddRange(BuildAccessRows(detail.Extras.AccessAdvice));

                previewWindow.MiningRows.Clear();
                previewWindow.MiningRows.AddRange(BuildMiningRows(detail.Extras.MiningSources));

                previewWindow.PriceRows.Clear();
                AddMaterialPreviewPriceRows(previewWindow, "buy", price.Buy);
                AddMaterialPreviewPriceRows(previewWindow, "sell", price.Sell);
            }
            catch (Exception ex)
            {
                if (!IsMaterialPreviewRequestCurrent(previewWindow, requestVersion))
                {
                    return;
                }

                previewWindow.Status = ex.Message;
                previewWindow.AccessRows.Clear();
                previewWindow.MiningRows.Clear();
                previewWindow.PriceRows.Clear();
            }
        }

        private async Task HandleSearchInputKeyUp(KeyboardEventArgs args)
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
            ClearMaterialPreview();
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
            ClearMaterialPreview();
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
            _missionDetailPanel = null;
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

                _missionDetailPanel = BuildMissionDetailPanel(detail);
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
                _missionDetailPanel = null;
                _missionDetailRows.Clear();
            }
        }

        private async Task OpenMissionDetailWindowAsync(MissionSourceVm mission)
        {
            BringFloatingWindowToFront(_missionDetailWindow);
            await LoadMissionDetailAsync(mission);
        }

        private void BeginMainTerminalDrag(Microsoft.AspNetCore.Components.Web.MouseEventArgs args)
        {
            BeginFloatingWindowDrag("main-terminal", args);
        }

        private void BeginFloatingWindowDrag(string windowKey, Microsoft.AspNetCore.Components.Web.MouseEventArgs args)
        {
            var window = GetFloatingWindow(windowKey);
            if (window is null)
            {
                return;
            }

            BringFloatingWindowToFront(window);
            _draggingWindowKey = windowKey;
            _dragStartClientX = args.ClientX;
            _dragStartClientY = args.ClientY;
            _dragStartWindowLeft = window.Left;
            _dragStartWindowTop = window.Top;
            _ = SetGlobalDragActiveAsync(true);
        }

        private void HandleFloatingWindowMouseMove(Microsoft.AspNetCore.Components.Web.MouseEventArgs args)
        {
            UpdateFloatingWindowPosition(args.ClientX, args.ClientY);
        }

        private void EndFloatingWindowDrag()
        {
            _ = SetGlobalDragActiveAsync(false);
            _draggingWindowKey = null;
        }

        [JSInvokable]
        public Task HandleGlobalWindowPointerMove(double clientX, double clientY)
        {
            if (UpdateFloatingWindowPosition(clientX, clientY))
            {
                return InvokeAsync(StateHasChanged);
            }

            return Task.CompletedTask;
        }

        [JSInvokable]
        public Task HandleGlobalWindowPointerUp()
        {
            EndFloatingWindowDrag();
            return InvokeAsync(StateHasChanged);
        }

        private bool UpdateFloatingWindowPosition(double clientX, double clientY)
        {
            if (string.IsNullOrWhiteSpace(_draggingWindowKey))
            {
                return false;
            }

            var window = GetFloatingWindow(_draggingWindowKey);
            if (window is null)
            {
                EndFloatingWindowDrag();
                return false;
            }

            var deltaX = clientX - _dragStartClientX;
            var deltaY = clientY - _dragStartClientY;
            window.Left = _dragStartWindowLeft + deltaX;
            window.Top = _dragStartWindowTop + deltaY;
            return true;
        }

        private ValueTask SetGlobalDragActiveAsync(bool isActive)
        {
            if (!_globalDragBridgeReady)
            {
                return ValueTask.CompletedTask;
            }

            return Js.InvokeVoidAsync("overlayFloatingWindows.setDragging", isActive);
        }

        private void ClearBlueprintState()
        {
            _blueprintHeader = null;
            _blueprintMaterials.Clear();
            _blueprintBaseStats.Clear();
            _blueprintModifierSlots.Clear();
            _missionSources.Clear();
        }

        private void ClearMaterialPreview()
        {
            if (_draggingWindowKey?.StartsWith("material:", StringComparison.Ordinal) == true)
            {
                EndFloatingWindowDrag();
            }

            _materialPreviewWindows.Clear();
        }

        private void CloseMaterialPreviewWindow(MaterialPreviewWindowVm previewWindow)
        {
            _materialPreviewWindows.Remove(previewWindow);
            if (_draggingWindowKey == $"material:{previewWindow.Key}")
            {
                EndFloatingWindowDrag();
            }
        }

        private void ClearMissionDetail()
        {
            _selectedMissionSourceId = null;
            _missionDetailTitle = string.Empty;
            _missionDetailSubtitle = string.Empty;
            _missionDetailDescription = string.Empty;
            _missionDetailStatus = string.Empty;
            _missionDetailPanel = null;
            _missionDetailRows.Clear();
            _missionRequestVersion++;

            if (_draggingWindowKey == "mission")
            {
                EndFloatingWindowDrag();
            }
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

        private bool IsMaterialPreviewRequestCurrent(MaterialPreviewWindowVm previewWindow, int requestVersion)
            => requestVersion == previewWindow.RequestVersion
               && _materialPreviewWindows.Contains(previewWindow);

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
                minQuality,
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

        private static MissionDetailPanelVm BuildMissionDetailPanel(OverlayMissionDetailResponse detail)
        {
            var englishName = !string.IsNullOrWhiteSpace(detail.Name) &&
                              !string.Equals(detail.Name, detail.NameChs, StringComparison.CurrentCultureIgnoreCase)
                ? detail.Name
                : null;

            return new MissionDetailPanelVm(
                englishName,
                FirstNonEmpty(detail.DescriptionChs, detail.Description),
                BuildMissionVersionTag(detail.GameVersion),
                BuildMissionHeaderTags(detail),
                BuildMissionStatCards(detail),
                BuildMissionRequirementLines(detail),
                BuildMissionRequirementTags(detail),
                BuildMissionRewardSections(detail),
                BuildMissionRequirementSections(detail),
                BuildMissionExtraSections(detail));
        }

        private static List<MissionTagVm> BuildMissionHeaderTags(OverlayMissionDetailResponse detail)
        {
            var tags = new List<MissionTagVm>();

            if ((detail.Extras?.Systems?.Count ?? 0) > 0)
            {
                tags.Add(new MissionTagVm(string.Join(" / ", detail.Extras!.Systems!), "mission-tag-system"));
            }

            var missionType = FirstNonEmpty(detail.MissionTypeChs, detail.MissionType);
            if (!string.IsNullOrWhiteSpace(missionType))
            {
                tags.Add(new MissionTagVm(missionType, "mission-tag-type"));
            }

            if (detail.Flags is not null)
            {
                tags.Add(detail.Flags.Illegal
                    ? new MissionTagVm("非法", "mission-tag-illegal")
                    : new MissionTagVm("合法", "mission-tag-legal"));
            }

            var category = FirstNonEmpty(detail.CategoryChs, detail.Category);
            if (!string.IsNullOrWhiteSpace(category))
            {
                tags.Add(new MissionTagVm(category, "mission-tag-category"));
            }

            return tags;
        }

        private static List<MissionStatCardVm> BuildMissionStatCards(OverlayMissionDetailResponse detail)
        {
            var cards = new List<MissionStatCardVm>();

            var rewardField = FindMissionField(detail, field =>
                string.Equals(field.Key, "rewardUec", StringComparison.OrdinalIgnoreCase) ||
                field.Label.Contains("奖励", StringComparison.CurrentCultureIgnoreCase));
            var rewardText = rewardField is null ? null : FormatDetailFieldValue(rewardField);
            if (!string.IsNullOrWhiteSpace(rewardText))
            {
                cards.Add(new MissionStatCardVm("奖励金额", rewardText, "mission-stat-gold"));
            }

            if (detail.Extras?.ScripAmount is decimal scripAmount && scripAmount > 0)
            {
                cards.Add(new MissionStatCardVm("Scrip", FormatNumber(scripAmount), "mission-stat-cyan"));
            }

            if (detail.Extras?.Reputation is { } reputation &&
                reputation.ValueKind == JsonValueKind.Object &&
                TryGetDecimal(reputation, "repPerMission", out var repPerMission) &&
                repPerMission > 0)
            {
                cards.Add(new MissionStatCardVm("任务经验", $"+{FormatNumber(repPerMission)} 声望", "mission-stat-green"));
            }

            return cards;
        }

        private static List<MissionInfoLineVm> BuildMissionRequirementLines(OverlayMissionDetailResponse detail)
        {
            var lines = new List<MissionInfoLineVm>();

            AddMissionRequirementField(lines, detail, "最低声望", "最低", "minStanding");
            AddMissionRequirementField(lines, detail, "最高声望", "最高", "maxStanding");
            AddMissionRequirementField(lines, detail, "完成时限", "时限", "timeToComplete", "time");

            return lines;
        }

        private static void AddMissionRequirementField(List<MissionInfoLineVm> lines, OverlayMissionDetailResponse detail, string lineLabel, params string[] patterns)
        {
            var field = FindMissionField(detail, candidate =>
                patterns.Any(pattern =>
                    candidate.Label.Contains(pattern, StringComparison.CurrentCultureIgnoreCase) ||
                    candidate.Key.Contains(pattern, StringComparison.OrdinalIgnoreCase)));
            var value = field is null ? null : FormatDetailFieldValue(field);
            if (!string.IsNullOrWhiteSpace(value) &&
                !lines.Any(existing => string.Equals(existing.Value, value, StringComparison.OrdinalIgnoreCase)))
            {
                lines.Add(new MissionInfoLineVm($"mission-requirement:{lines.Count}:{lineLabel}", lineLabel, value));
            }
        }

        private static List<MissionTagVm> BuildMissionRequirementTags(OverlayMissionDetailResponse detail)
        {
            var tags = new List<MissionTagVm>();

            if (detail.Flags is not null)
            {
                tags.Add(detail.Flags.CanBeShared
                    ? new MissionTagVm("可共享: 是", "mission-tag-legal")
                    : new MissionTagVm("可共享: 否", "mission-tag-illegal"));
                tags.Add(detail.Flags.Illegal
                    ? new MissionTagVm("非法: 是", "mission-tag-illegal")
                    : new MissionTagVm("非法: 否", "mission-tag-legal"));
            }

            return tags;
        }

        private static List<MissionSectionVm> BuildMissionRewardSections(OverlayMissionDetailResponse detail)
        {
            var sections = new List<MissionSectionVm>();

            if (detail.Extras?.ReceiveItems is { } receiveItems &&
                TryBuildMissionItemSection(receiveItems, "你将获得", "mission-reward-items", out var receiveSection))
            {
                sections.Add(receiveSection);
            }

            if (detail.Extras?.BlueprintRewards is { } blueprintRewards &&
                TryBuildMissionBlueprintRewardSection(blueprintRewards, out var blueprintSection))
            {
                sections.Add(blueprintSection);
            }

            return sections;
        }

        private static List<MissionSectionVm> BuildMissionRequirementSections(OverlayMissionDetailResponse detail)
        {
            var sections = new List<MissionSectionVm>();

            if (detail.Extras?.RequiredItems is { } requiredItems &&
                TryBuildMissionItemSection(requiredItems, "提交要求", "mission-required-items", out var requiredSection))
            {
                sections.Add(requiredSection);
            }

            if (detail.Extras?.RequiredIntros is { } requiredIntros &&
                TryBuildMissionNameSection(requiredIntros, "前置引导任务", "mission-required-intros", out var introSection))
            {
                sections.Add(introSection);
            }

            if (detail.Extras?.UnlockMissions is { } unlockMissions &&
                TryBuildMissionNameSection(unlockMissions, "解锁后续任务", "mission-unlock-missions", out var unlockSection))
            {
                sections.Add(unlockSection);
            }

            return sections;
        }

        private static List<MissionSectionVm> BuildMissionExtraSections(OverlayMissionDetailResponse detail)
        {
            var sections = new List<MissionSectionVm>();

            sections.AddRange(BuildMissionBaseInfoSections(detail));

            if (detail.Extras?.Reputation is { } reputation &&
                TryBuildMissionReputationSection(reputation, out var reputationSection))
            {
                sections.Add(reputationSection);
            }

            if (detail.Extras?.Combat is { } combat &&
                TryBuildMissionCombatSections(combat, out var combatSections))
            {
                sections.AddRange(combatSections);
            }

            var versionItems = new List<MissionSectionItemVm>();
            if (!string.IsNullOrWhiteSpace(detail.GameVersion))
            {
                versionItems.Add(new MissionSectionItemVm("mission-version", "游戏版本", detail.GameVersion));
            }

            if (!string.IsNullOrWhiteSpace(detail.Id))
            {
                versionItems.Add(new MissionSectionItemVm("mission-id", "任务 ID", detail.Id));
            }

            if (versionItems.Count > 0)
            {
                sections.Add(new MissionSectionVm("mission-version-section", "版本信息", MissionSectionKind.Info, versionItems));
            }

            if (detail.Extras?.FailReputationAmounts is { } failPenalties &&
                TryBuildMissionPenaltySection(failPenalties, out var penaltySection))
            {
                sections.Add(penaltySection);
            }

            return sections;
        }

        private static List<MissionSectionVm> BuildMissionBaseInfoSections(OverlayMissionDetailResponse detail)
        {
            var sections = new List<MissionSectionVm>();

            foreach (var section in (detail.Sections ?? []).Where(section => (section.Items?.Count ?? 0) > 0))
            {
                var items = new List<MissionSectionItemVm>();
                foreach (var item in section.Items ?? [])
                {
                    if (string.Equals(item.Key, "rewardUec", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (item.Label.Contains("最低", StringComparison.CurrentCultureIgnoreCase) ||
                        item.Label.Contains("最高", StringComparison.CurrentCultureIgnoreCase) ||
                        item.Label.Contains("时限", StringComparison.CurrentCultureIgnoreCase))
                    {
                        continue;
                    }

                    var value = FormatDetailFieldValue(item);
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        continue;
                    }

                    items.Add(new MissionSectionItemVm(
                        $"mission-section:{section.Key}:{item.Key}:{items.Count}",
                        item.Label,
                        value));
                }

                if (items.Count > 0)
                {
                    sections.Add(new MissionSectionVm(
                        $"mission-section:{section.Key}",
                        string.IsNullOrWhiteSpace(section.Title) ? "基本信息" : section.Title,
                        MissionSectionKind.Info,
                        items));
                }
            }

            return sections;
        }

        private static bool TryBuildMissionItemSection(JsonElement items, string title, string keyPrefix, out MissionSectionVm section)
        {
            var rows = new List<MissionSectionItemVm>();

            if (items.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
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

                    rows.Add(new MissionSectionItemVm($"{keyPrefix}:{index++}", name, secondary));
                }
            }

            if (rows.Count == 0)
            {
                section = default!;
                return false;
            }

            section = new MissionSectionVm(keyPrefix, title, MissionSectionKind.List, rows);
            return true;
        }

        private static bool TryBuildMissionNameSection(JsonElement items, string title, string keyPrefix, out MissionSectionVm section)
        {
            var rows = new List<MissionSectionItemVm>();

            if (items.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var item in items.EnumerateArray().Take(16))
                {
                    string? name = item.ValueKind switch
                    {
                        JsonValueKind.String => item.GetString(),
                        JsonValueKind.Object => FirstJsonString(item, "nameChs", "name", "titleChs", "title"),
                        _ => null
                    };

                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    rows.Add(new MissionSectionItemVm($"{keyPrefix}:{index++}", name, null));
                }
            }

            if (rows.Count == 0)
            {
                section = default!;
                return false;
            }

            section = new MissionSectionVm(keyPrefix, title, MissionSectionKind.List, rows);
            return true;
        }

        private static bool TryBuildMissionBlueprintRewardSection(JsonElement blueprintRewards, out MissionSectionVm section)
        {
            var summaryParts = new List<string>();
            var items = new List<MissionSectionItemVm>();

            if (blueprintRewards.ValueKind == JsonValueKind.Object &&
                blueprintRewards.TryGetProperty("summary", out var summary) &&
                summary.ValueKind == JsonValueKind.Object)
            {
                if (TryGetInt(summary, "poolItemCount", out var poolItemCount) && poolItemCount > 0)
                {
                    summaryParts.Add($"共 {poolItemCount} 项");
                }

                if (TryGetDecimal(summary, "poolChancePercent", out var poolChancePercent) && poolChancePercent > 0)
                {
                    summaryParts.Add($"触发 {FormatNumber(poolChancePercent)}%");
                }
            }

            if (blueprintRewards.ValueKind == JsonValueKind.Object &&
                blueprintRewards.TryGetProperty("items", out var rewardItems) &&
                rewardItems.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var item in rewardItems.EnumerateArray().Take(12))
                {
                    var name = FirstJsonString(item, "nameChs", "name");
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    var secondary = TryGetDecimal(item, "chancePercent", out var chancePercent) && chancePercent > 0
                        ? $"{FormatNumber(chancePercent)}%"
                        : null;

                    items.Add(new MissionSectionItemVm($"mission-blueprint-reward:{index++}", name, secondary));
                }
            }

            if (items.Count == 0)
            {
                section = default!;
                return false;
            }

            section = new MissionSectionVm(
                "mission-blueprint-rewards",
                "蓝图奖励",
                MissionSectionKind.List,
                items,
                summaryParts.Count == 0 ? null : string.Join(" · ", summaryParts));
            return true;
        }

        private static bool TryBuildMissionReputationSection(JsonElement reputation, out MissionSectionVm section)
        {
            var items = new List<MissionSectionItemVm>();

            if (reputation.ValueKind == JsonValueKind.Object)
            {
                var faction = FirstJsonString(reputation, "factionName");
                if (!string.IsNullOrWhiteSpace(faction))
                {
                    items.Add(new MissionSectionItemVm("mission-reputation-faction", "阵营", faction));
                }

                var scope = FirstJsonString(reputation, "scopeName");
                if (!string.IsNullOrWhiteSpace(scope))
                {
                    items.Add(new MissionSectionItemVm("mission-reputation-scope", "声望域", scope));
                }

                if (TryGetDecimal(reputation, "repPerMission", out var repPerMission) && repPerMission > 0)
                {
                    items.Add(new MissionSectionItemVm("mission-reputation-per", "每次任务", $"+{FormatNumber(repPerMission)} 声望"));
                }

                if (reputation.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array)
                {
                    var index = 0;
                    foreach (var row in rows.EnumerateArray().Take(10))
                    {
                        var rankName = FirstJsonString(row, "rankName");
                        if (string.IsNullOrWhiteSpace(rankName))
                        {
                            continue;
                        }

                        var parts = new List<string>();
                        var xpText = FirstJsonString(row, "xpText");
                        var missionsText = FirstJsonString(row, "missionsText");

                        if (!string.IsNullOrWhiteSpace(xpText))
                        {
                            parts.Add($"XP {xpText}");
                        }

                        if (!string.IsNullOrWhiteSpace(missionsText))
                        {
                            parts.Add($"任务 {missionsText}");
                        }

                        if (TryGetBoolean(row, "isCurrent", out var isCurrent) && isCurrent)
                        {
                            parts.Add("当前等级");
                        }

                        if (TryGetBoolean(row, "isMin", out var isMin) && isMin)
                        {
                            parts.Add("最低要求");
                        }

                        if (TryGetBoolean(row, "isMax", out var isMax) && isMax)
                        {
                            parts.Add("最高等级");
                        }

                        items.Add(new MissionSectionItemVm(
                            $"mission-reputation-row:{index++}",
                            rankName,
                            parts.Count == 0 ? null : string.Join(" · ", parts)));
                    }
                }
            }

            if (items.Count == 0)
            {
                section = default!;
                return false;
            }

            section = new MissionSectionVm("mission-reputation", "声望", MissionSectionKind.List, items);
            return true;
        }

        private static bool TryBuildMissionCombatSections(JsonElement combat, out List<MissionSectionVm> sections)
        {
            sections = [];
            if (combat.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var summaryItems = new List<MissionSectionItemVm>();

            var hostileText = BuildMissionCombatRangeText(combat, "hostileTotal", "hostileMin", "hostileMax");
            if (!string.IsNullOrWhiteSpace(hostileText))
            {
                summaryItems.Add(new MissionSectionItemVm("mission-combat-hostile", "敌对目标", hostileText));
            }

            var friendlyText = BuildMissionCombatRangeText(combat, null, "friendlyMin", "friendlyMax");
            if (!string.IsNullOrWhiteSpace(friendlyText))
            {
                summaryItems.Add(new MissionSectionItemVm("mission-combat-friendly", "友军数量", friendlyText));
            }

            if (summaryItems.Count > 0)
            {
                sections.Add(new MissionSectionVm("mission-combat-summary", "战斗概览", MissionSectionKind.Info, summaryItems));
            }

            if (combat.TryGetProperty("groups", out var groups) && groups.ValueKind == JsonValueKind.Array)
            {
                var groupItems = new List<MissionSectionItemVm>();
                var index = 0;
                foreach (var group in groups.EnumerateArray().Take(12))
                {
                    var roleName = TranslateMissionCombatRole(FirstJsonString(group, "role"));
                    if (string.IsNullOrWhiteSpace(roleName))
                    {
                        roleName = $"分组 {index + 1}";
                    }

                    var parts = new List<string>();
                    var rangeText = BuildMissionCombatItemRangeText(group, "min", "max");
                    if (!string.IsNullOrWhiteSpace(rangeText))
                    {
                        parts.Add($"{rangeText} 艘");
                    }

                    if (TryGetDecimal(group, "spawnChance", out var spawnChance) && spawnChance > 0)
                    {
                        parts.Add($"{FormatNumber(spawnChance <= 1 ? spawnChance * 100 : spawnChance)}%");
                    }

                    groupItems.Add(new MissionSectionItemVm(
                        $"mission-combat-group:{index++}",
                        roleName,
                        parts.Count == 0 ? null : string.Join(" · ", parts)));
                }

                if (groupItems.Count > 0)
                {
                    sections.Add(new MissionSectionVm("mission-combat-groups", "战斗分组", MissionSectionKind.List, groupItems));
                }
            }

            if (combat.TryGetProperty("shipPool", out var shipPool) && shipPool.ValueKind == JsonValueKind.Array)
            {
                var shipItems = new List<MissionSectionItemVm>();
                var index = 0;
                foreach (var ship in shipPool.EnumerateArray().Take(20))
                {
                    var name = FirstJsonString(ship, "nameChs", "name");
                    if (string.IsNullOrWhiteSpace(name) && ship.ValueKind == JsonValueKind.String)
                    {
                        name = ship.GetString();
                    }

                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    shipItems.Add(new MissionSectionItemVm($"mission-combat-ship:{index++}", name, null));
                }

                if (shipItems.Count > 0)
                {
                    sections.Add(new MissionSectionVm("mission-combat-ships", "可能出现的舰船", MissionSectionKind.List, shipItems));
                }
            }

            return sections.Count > 0;
        }

        private static bool TryBuildMissionPenaltySection(JsonElement penalties, out MissionSectionVm section)
        {
            var items = new List<MissionSectionItemVm>();

            if (penalties.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var penalty in penalties.EnumerateArray().Take(12))
                {
                    var text = penalty.ValueKind switch
                    {
                        JsonValueKind.Object => FirstJsonString(penalty, "amount", "value", "text"),
                        JsonValueKind.String => penalty.GetString(),
                        JsonValueKind.Number => penalty.ToString(),
                        _ => null
                    };

                    if (string.IsNullOrWhiteSpace(text))
                    {
                        continue;
                    }

                    items.Add(new MissionSectionItemVm($"mission-penalty:{index++}", text, null));
                }
            }

            if (items.Count == 0)
            {
                section = default!;
                return false;
            }

            section = new MissionSectionVm("mission-penalties", "失败惩罚", MissionSectionKind.List, items);
            return true;
        }

        private static string BuildMissionCombatRangeText(JsonElement element, string? totalName, string minName, string maxName)
        {
            if (!string.IsNullOrWhiteSpace(totalName) &&
                TryGetInt(element, totalName, out var total) &&
                total > 0)
            {
                return $"{total}";
            }

            return BuildMissionCombatItemRangeText(element, minName, maxName);
        }

        private static string BuildMissionCombatItemRangeText(JsonElement element, string minName, string maxName)
        {
            var hasMin = TryGetInt(element, minName, out var min);
            var hasMax = TryGetInt(element, maxName, out var max);

            if (!hasMin && !hasMax)
            {
                return string.Empty;
            }

            if (!hasMin) min = max;
            if (!hasMax) max = min;
            return min == max ? $"{max}" : $"{min}-{max}";
        }

        private static string BuildMissionVersionTag(string? gameVersion)
        {
            if (string.IsNullOrWhiteSpace(gameVersion))
            {
                return string.Empty;
            }

            var separatorIndex = gameVersion.IndexOf('-', StringComparison.Ordinal);
            return separatorIndex > 0 ? gameVersion[..separatorIndex] : gameVersion;
        }

        private static string TranslateMissionCombatRole(string? role)
        {
            return role switch
            {
                "EnemyShips" => "敌方舰船",
                "InitialEnemies" => "初始敌人",
                "EscortShip" => "护航舰船",
                "InterdictionShips" => "拦截舰船",
                "EscortReinforcements" => "护航增援",
                "WaveShips" => "波次舰船",
                "CargoShip" => "货运舰船",
                "MissionTargets" => "任务目标",
                "SalvageSpawnDescription" => "打捞目标",
                "ChickenShipSpawnDescription" => "安保舰船",
                _ => string.IsNullOrWhiteSpace(role) ? string.Empty : role
            };
        }

        private static OverlayDetailField? FindMissionField(OverlayMissionDetailResponse detail, Func<OverlayDetailField, bool> predicate)
        {
            foreach (var section in detail.Sections ?? [])
            {
                foreach (var field in section.Items ?? [])
                {
                    if (predicate(field))
                    {
                        return field;
                    }
                }
            }

            return null;
        }

#if false
        private static List<MissionTagVm> BuildMissionHeaderTags(OverlayMissionDetailResponse detail)
        {
            var tags = new List<MissionTagVm>();

            if ((detail.Extras?.Systems?.Count ?? 0) > 0)
            {
                tags.Add(new MissionTagVm(string.Join(" / ", detail.Extras!.Systems!), "mission-tag-system"));
            }

            var missionType = FirstNonEmpty(detail.MissionTypeChs, detail.MissionType);
            if (!string.IsNullOrWhiteSpace(missionType))
            {
                tags.Add(new MissionTagVm(missionType, "mission-tag-type"));
            }

            if (detail.Flags is not null)
            {
                tags.Add(detail.Flags.Illegal
                    ? new MissionTagVm("非法", "mission-tag-illegal")
                    : new MissionTagVm("合法", "mission-tag-legal"));
            }

            var category = FirstNonEmpty(detail.CategoryChs, detail.Category);
            if (!string.IsNullOrWhiteSpace(category))
            {
                tags.Add(new MissionTagVm(category, "mission-tag-category"));
            }

            return tags;
        }

        private static List<MissionStatCardVm> BuildMissionStatCards(OverlayMissionDetailResponse detail)
        {
            var cards = new List<MissionStatCardVm>();

            var rewardField = FindMissionField(detail, field =>
                string.Equals(field.Key, "rewardUec", StringComparison.OrdinalIgnoreCase) ||
                field.Label.Contains("奖励", StringComparison.CurrentCultureIgnoreCase));
            var rewardText = rewardField is null ? null : FormatDetailFieldValue(rewardField);
            if (!string.IsNullOrWhiteSpace(rewardText))
            {
                cards.Add(new MissionStatCardVm("奖励金额", rewardText, "mission-stat-gold"));
            }

            if (detail.Extras?.ScripAmount is decimal scripAmount && scripAmount > 0)
            {
                cards.Add(new MissionStatCardVm("Scrip", FormatNumber(scripAmount), "mission-stat-cyan"));
            }

            if (detail.Extras?.Reputation is { } reputation &&
                reputation.ValueKind == JsonValueKind.Object &&
                TryGetDecimal(reputation, "repPerMission", out var repPerMission) &&
                repPerMission > 0)
            {
                cards.Add(new MissionStatCardVm("任务经验", $"+{FormatNumber(repPerMission)} 声望", "mission-stat-green"));
            }

            return cards;
        }

        private static List<MissionInfoLineVm> BuildMissionRequirementLines(OverlayMissionDetailResponse detail)
        {
            var lines = new List<MissionInfoLineVm>();

            AddMissionRequirementField(lines, detail, "最低声望", "最低", "minStanding");
            AddMissionRequirementField(lines, detail, "最高声望", "最高", "maxStanding");
            AddMissionRequirementField(lines, detail, "完成时限", "时限", "time");
            AddMissionRequirementField(lines, detail, "时限", "完成时间", "timeToComplete");

            if ((detail.Extras?.Systems?.Count ?? 0) > 0)
            {
                lines.Add(new MissionInfoLineVm("mission-systems", "星系", string.Join(" / ", detail.Extras!.Systems!)));
            }

            return lines;
        }

        private static void AddMissionRequirementField(List<MissionInfoLineVm> lines, OverlayMissionDetailResponse detail, string lineLabel, params string[] patterns)
        {
            var field = FindMissionField(detail, candidate =>
                patterns.Any(pattern =>
                    candidate.Label.Contains(pattern, StringComparison.CurrentCultureIgnoreCase) ||
                    candidate.Key.Contains(pattern, StringComparison.OrdinalIgnoreCase)));
            var value = field is null ? null : FormatDetailFieldValue(field);
            if (!string.IsNullOrWhiteSpace(value) &&
                !lines.Any(existing => string.Equals(existing.Value, value, StringComparison.OrdinalIgnoreCase)))
            {
                lines.Add(new MissionInfoLineVm($"mission-requirement:{lines.Count}:{lineLabel}", lineLabel, value));
            }
        }

        private static List<MissionTagVm> BuildMissionRequirementTags(OverlayMissionDetailResponse detail)
        {
            var tags = new List<MissionTagVm>();

            if (detail.Flags is not null)
            {
                tags.Add(detail.Flags.CanBeShared
                    ? new MissionTagVm("可共享: 是", "mission-tag-legal")
                    : new MissionTagVm("可共享: 否", "mission-tag-illegal"));
                tags.Add(detail.Flags.Illegal
                    ? new MissionTagVm("非法: 是", "mission-tag-illegal")
                    : new MissionTagVm("非法: 否", "mission-tag-legal"));
            }

            if (!string.IsNullOrWhiteSpace(detail.SourceType))
            {
                tags.Add(new MissionTagVm(detail.SourceType!, "mission-tag-neutral"));
            }

            return tags;
        }

        private static List<MissionSectionVm> BuildMissionRewardSections(OverlayMissionDetailResponse detail)
        {
            var sections = new List<MissionSectionVm>();

            if (detail.Extras?.ReceiveItems is { } receiveItems &&
                TryBuildMissionItemSection(receiveItems, "你将获得", "mission-reward-items", out var receiveSection))
            {
                sections.Add(receiveSection);
            }

            if (detail.Extras?.BlueprintRewards is { } blueprintRewards &&
                TryBuildMissionBlueprintRewardSection(blueprintRewards, out var blueprintSection))
            {
                sections.Add(blueprintSection);
            }

            return sections;
        }

        private static List<MissionSectionVm> BuildMissionRequirementSections(OverlayMissionDetailResponse detail)
        {
            var sections = new List<MissionSectionVm>();

            if (detail.Extras?.RequiredItems is { } requiredItems &&
                TryBuildMissionItemSection(requiredItems, "所需物品", "mission-required-items", out var requiredSection))
            {
                sections.Add(requiredSection);
            }

            if (detail.Extras?.RequiredIntros is { } requiredIntros &&
                TryBuildMissionNameSection(requiredIntros, "前置引导任务", "mission-required-intros", out var introSection))
            {
                sections.Add(introSection);
            }

            if (detail.Extras?.UnlockMissions is { } unlockMissions &&
                TryBuildMissionNameSection(unlockMissions, "解锁后续任务", "mission-unlock-missions", out var unlockSection))
            {
                sections.Add(unlockSection);
            }

            return sections;
        }

        private static List<MissionSectionVm> BuildMissionExtraSections(OverlayMissionDetailResponse detail)
        {
            var sections = new List<MissionSectionVm>();

            sections.AddRange(BuildMissionBaseInfoSections(detail));

            if (detail.Extras?.Reputation is { } reputation &&
                TryBuildMissionReputationSection(reputation, out var reputationSection))
            {
                sections.Add(reputationSection);
            }

            if (detail.Extras?.Combat is { } combat &&
                TryBuildMissionCombatSections(combat, out var combatSections))
            {
                sections.AddRange(combatSections);
            }

            var versionItems = new List<MissionSectionItemVm>();
            if (!string.IsNullOrWhiteSpace(detail.GameVersion))
            {
                versionItems.Add(new MissionSectionItemVm("mission-version", "游戏版本", detail.GameVersion));
            }

            if (!string.IsNullOrWhiteSpace(detail.Id))
            {
                versionItems.Add(new MissionSectionItemVm("mission-id", "任务 ID", detail.Id));
            }

            if (versionItems.Count > 0)
            {
                sections.Add(new MissionSectionVm("mission-version-section", "版本信息", MissionSectionKind.Info, versionItems));
            }

            if (detail.Extras?.FailReputationAmounts is { } failPenalties &&
                TryBuildMissionPenaltySection(failPenalties, out var penaltySection))
            {
                sections.Add(penaltySection);
            }

            return sections;
        }

        private static List<MissionSectionVm> BuildMissionBaseInfoSections(OverlayMissionDetailResponse detail)
        {
            var sections = new List<MissionSectionVm>();

            foreach (var section in (detail.Sections ?? []).Where(section => (section.Items?.Count ?? 0) > 0))
            {
                var items = new List<MissionSectionItemVm>();
                foreach (var item in section.Items ?? [])
                {
                    if (string.Equals(item.Key, "rewardUec", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (item.Label.Contains("最低", StringComparison.CurrentCultureIgnoreCase) ||
                        item.Label.Contains("最高", StringComparison.CurrentCultureIgnoreCase) ||
                        item.Label.Contains("时限", StringComparison.CurrentCultureIgnoreCase))
                    {
                        continue;
                    }

                    var value = FormatDetailFieldValue(item);
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        continue;
                    }

                    items.Add(new MissionSectionItemVm(
                        $"mission-section:{section.Key}:{item.Key}:{items.Count}",
                        item.Label,
                        value));
                }

                if (items.Count > 0)
                {
                    sections.Add(new MissionSectionVm(
                        $"mission-section:{section.Key}",
                        string.IsNullOrWhiteSpace(section.Title) ? "基础信息" : section.Title,
                        MissionSectionKind.Info,
                        items));
                }
            }

            return sections;
        }

        private static bool TryBuildMissionItemSection(JsonElement items, string title, string keyPrefix, out MissionSectionVm section)
        {
            var rows = new List<MissionSectionItemVm>();

            if (items.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
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

                    rows.Add(new MissionSectionItemVm($"{keyPrefix}:{index++}", name, secondary));
                }
            }

            if (rows.Count == 0)
            {
                section = default!;
                return false;
            }

            section = new MissionSectionVm(keyPrefix, title, MissionSectionKind.List, rows);
            return true;
        }

        private static bool TryBuildMissionNameSection(JsonElement items, string title, string keyPrefix, out MissionSectionVm section)
        {
            var rows = new List<MissionSectionItemVm>();

            if (items.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var item in items.EnumerateArray().Take(16))
                {
                    string? name = item.ValueKind switch
                    {
                        JsonValueKind.String => item.GetString(),
                        JsonValueKind.Object => FirstJsonString(item, "nameChs", "name", "titleChs", "title"),
                        _ => null
                    };

                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    rows.Add(new MissionSectionItemVm($"{keyPrefix}:{index++}", name, null));
                }
            }

            if (rows.Count == 0)
            {
                section = default!;
                return false;
            }

            section = new MissionSectionVm(keyPrefix, title, MissionSectionKind.List, rows);
            return true;
        }

        private static bool TryBuildMissionBlueprintRewardSection(JsonElement blueprintRewards, out MissionSectionVm section)
        {
            var summaryParts = new List<string>();
            var items = new List<MissionSectionItemVm>();

            if (blueprintRewards.ValueKind == JsonValueKind.Object &&
                blueprintRewards.TryGetProperty("summary", out var summary) &&
                summary.ValueKind == JsonValueKind.Object)
            {
                if (TryGetInt(summary, "poolItemCount", out var poolItemCount) && poolItemCount > 0)
                {
                    summaryParts.Add($"共 {poolItemCount} 项");
                }

                if (TryGetDecimal(summary, "poolChancePercent", out var poolChancePercent) && poolChancePercent > 0)
                {
                    summaryParts.Add($"触发 {FormatNumber(poolChancePercent)}%");
                }
            }

            if (blueprintRewards.ValueKind == JsonValueKind.Object &&
                blueprintRewards.TryGetProperty("items", out var rewardItems) &&
                rewardItems.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var item in rewardItems.EnumerateArray().Take(12))
                {
                    var name = FirstJsonString(item, "nameChs", "name");
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    var secondary = TryGetDecimal(item, "chancePercent", out var chancePercent) && chancePercent > 0
                        ? $"{FormatNumber(chancePercent)}%"
                        : null;

                    items.Add(new MissionSectionItemVm($"mission-blueprint-reward:{index++}", name, secondary));
                }
            }

            if (items.Count == 0)
            {
                section = default!;
                return false;
            }

            section = new MissionSectionVm(
                "mission-blueprint-rewards",
                "蓝图奖励",
                MissionSectionKind.List,
                items,
                summaryParts.Count == 0 ? null : string.Join(" · ", summaryParts));
            return true;
        }

        private static bool TryBuildMissionReputationSection(JsonElement reputation, out MissionSectionVm section)
        {
            var items = new List<MissionSectionItemVm>();

            if (reputation.ValueKind == JsonValueKind.Object)
            {
                var faction = FirstJsonString(reputation, "factionName");
                if (!string.IsNullOrWhiteSpace(faction))
                {
                    items.Add(new MissionSectionItemVm("mission-reputation-faction", "阵营", faction));
                }

                var scope = FirstJsonString(reputation, "scopeName");
                if (!string.IsNullOrWhiteSpace(scope))
                {
                    items.Add(new MissionSectionItemVm("mission-reputation-scope", "声望域", scope));
                }

                if (TryGetDecimal(reputation, "repPerMission", out var repPerMission) && repPerMission > 0)
                {
                    items.Add(new MissionSectionItemVm("mission-reputation-per", "每次任务", $"+{FormatNumber(repPerMission)} 声望"));
                }

                if (reputation.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array)
                {
                    var index = 0;
                    foreach (var row in rows.EnumerateArray().Take(10))
                    {
                        var rankName = FirstJsonString(row, "rankName");
                        if (string.IsNullOrWhiteSpace(rankName))
                        {
                            continue;
                        }

                        var parts = new List<string>();
                        var xpText = FirstJsonString(row, "xpText");
                        var missionsText = FirstJsonString(row, "missionsText");

                        if (!string.IsNullOrWhiteSpace(xpText))
                        {
                            parts.Add($"XP {xpText}");
                        }

                        if (!string.IsNullOrWhiteSpace(missionsText))
                        {
                            parts.Add($"任务 {missionsText}");
                        }

                        if (TryGetBoolean(row, "isCurrent", out var isCurrent) && isCurrent)
                        {
                            parts.Add("当前等级");
                        }

                        if (TryGetBoolean(row, "isMin", out var isMin) && isMin)
                        {
                            parts.Add("最低要求");
                        }

                        if (TryGetBoolean(row, "isMax", out var isMax) && isMax)
                        {
                            parts.Add("最高等级");
                        }

                        items.Add(new MissionSectionItemVm(
                            $"mission-reputation-row:{index++}",
                            rankName,
                            parts.Count == 0 ? null : string.Join(" · ", parts)));
                    }
                }
            }

            if (items.Count == 0)
            {
                section = default!;
                return false;
            }

            section = new MissionSectionVm("mission-reputation", "声望", MissionSectionKind.List, items);
            return true;
        }

        private static bool TryBuildMissionCombatSections(JsonElement combat, out List<MissionSectionVm> sections)
        {
            sections = [];
            if (combat.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var summaryItems = new List<MissionSectionItemVm>();

            var hostileText = BuildMissionCombatRangeText(combat, "hostileTotal", "hostileMin", "hostileMax");
            if (!string.IsNullOrWhiteSpace(hostileText))
            {
                summaryItems.Add(new MissionSectionItemVm("mission-combat-hostile", "敌对目标", hostileText));
            }

            var friendlyText = BuildMissionCombatRangeText(combat, null, "friendlyMin", "friendlyMax");
            if (!string.IsNullOrWhiteSpace(friendlyText))
            {
                summaryItems.Add(new MissionSectionItemVm("mission-combat-friendly", "友军数量", friendlyText));
            }

            if (summaryItems.Count > 0)
            {
                sections.Add(new MissionSectionVm("mission-combat-summary", "战斗概览", MissionSectionKind.Info, summaryItems));
            }

            if (combat.TryGetProperty("groups", out var groups) && groups.ValueKind == JsonValueKind.Array)
            {
                var groupItems = new List<MissionSectionItemVm>();
                var index = 0;
                foreach (var group in groups.EnumerateArray().Take(12))
                {
                    var roleName = TranslateMissionCombatRole(FirstJsonString(group, "role"));
                    if (string.IsNullOrWhiteSpace(roleName))
                    {
                        roleName = $"分组 {index + 1}";
                    }

                    var parts = new List<string>();
                    var rangeText = BuildMissionCombatItemRangeText(group, "min", "max");
                    if (!string.IsNullOrWhiteSpace(rangeText))
                    {
                        parts.Add(rangeText);
                    }

                    if (TryGetDecimal(group, "spawnChance", out var spawnChance) && spawnChance > 0)
                    {
                        parts.Add($"{FormatNumber(spawnChance <= 1 ? spawnChance * 100 : spawnChance)}%");
                    }

                    groupItems.Add(new MissionSectionItemVm(
                        $"mission-combat-group:{index++}",
                        roleName,
                        parts.Count == 0 ? null : string.Join(" · ", parts)));
                }

                if (groupItems.Count > 0)
                {
                    sections.Add(new MissionSectionVm("mission-combat-groups", "战斗分组", MissionSectionKind.List, groupItems));
                }
            }

            if (combat.TryGetProperty("shipPool", out var shipPool) && shipPool.ValueKind == JsonValueKind.Array)
            {
                var shipItems = new List<MissionSectionItemVm>();
                var index = 0;
                foreach (var ship in shipPool.EnumerateArray().Take(20))
                {
                    var name = FirstJsonString(ship, "nameChs", "name");
                    if (string.IsNullOrWhiteSpace(name) && ship.ValueKind == JsonValueKind.String)
                    {
                        name = ship.GetString();
                    }

                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    shipItems.Add(new MissionSectionItemVm($"mission-combat-ship:{index++}", name, null));
                }

                if (shipItems.Count > 0)
                {
                    sections.Add(new MissionSectionVm("mission-combat-ships", "可能出现的舰船", MissionSectionKind.List, shipItems));
                }
            }

            return sections.Count > 0;
        }

        private static bool TryBuildMissionPenaltySection(JsonElement penalties, out MissionSectionVm section)
        {
            var items = new List<MissionSectionItemVm>();

            if (penalties.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var penalty in penalties.EnumerateArray().Take(12))
                {
                    var text = penalty.ValueKind switch
                    {
                        JsonValueKind.Object => FirstJsonString(penalty, "amount", "value", "text"),
                        JsonValueKind.String => penalty.GetString(),
                        JsonValueKind.Number => penalty.ToString(),
                        _ => null
                    };

                    if (string.IsNullOrWhiteSpace(text))
                    {
                        continue;
                    }

                    items.Add(new MissionSectionItemVm($"mission-penalty:{index++}", text, null));
                }
            }

            if (items.Count == 0)
            {
                section = default!;
                return false;
            }

            section = new MissionSectionVm("mission-penalties", "失败惩罚", MissionSectionKind.List, items);
            return true;
        }

        private static string BuildMissionCombatRangeText(JsonElement element, string? totalName, string minName, string maxName)
        {
            if (!string.IsNullOrWhiteSpace(totalName) &&
                TryGetInt(element, totalName, out var total) &&
                total > 0)
            {
                return $"{total}";
            }

            return BuildMissionCombatItemRangeText(element, minName, maxName);
        }

        private static string BuildMissionCombatItemRangeText(JsonElement element, string minName, string maxName)
        {
            var hasMin = TryGetInt(element, minName, out var min);
            var hasMax = TryGetInt(element, maxName, out var max);

            if (!hasMin && !hasMax)
            {
                return string.Empty;
            }

            if (!hasMin) min = max;
            if (!hasMax) max = min;
            return min == max ? $"{max}" : $"{min}-{max}";
        }

        private static string BuildMissionVersionTag(string? gameVersion)
        {
            if (string.IsNullOrWhiteSpace(gameVersion))
            {
                return string.Empty;
            }

            var separatorIndex = gameVersion.IndexOf('-', StringComparison.Ordinal);
            return separatorIndex > 0 ? gameVersion[..separatorIndex] : gameVersion;
        }

        private static string TranslateMissionCombatRole(string? role)
        {
            return role switch
            {
                "EnemyShips" => "敌方舰船",
                "InitialEnemies" => "初始敌人",
                "EscortShip" => "护航舰船",
                "InterdictionShips" => "拦截舰船",
                "EscortReinforcements" => "护航增援",
                "WaveShips" => "波次舰船",
                "CargoShip" => "货运舰船",
                "MissionTargets" => "任务目标",
                "SalvageSpawnDescription" => "打捞目标",
                "ChickenShipSpawnDescription" => "安保舰船",
                _ => string.IsNullOrWhiteSpace(role) ? string.Empty : role
            };
        }

        private static OverlayDetailField? FindMissionField(OverlayMissionDetailResponse detail, Func<OverlayDetailField, bool> predicate)
        {
            foreach (var section in detail.Sections ?? [])
            {
                foreach (var field in section.Items ?? [])
                {
                    if (predicate(field))
                    {
                        return field;
                    }
                }
            }

            return null;
        }

#endif
        private static List<MissionRowVm> BuildMissionRows(OverlayMissionDetailResponse detail)
        {
            var rows = new List<MissionRowVm>();
            var index = 0;

            MissionRowVm Section(string title)
                => new($"mission-section:{index++}", MissionRowKind.Section, title);

            MissionRowVm Text(string text, string? secondary = null)
                => new($"mission-text:{index++}", MissionRowKind.Text, text, secondary);

            foreach (var section in (detail.Sections ?? []).Where(section => (section.Items?.Count ?? 0) > 0))
            {
                rows.Add(Section(section.Title));

                foreach (var item in section.Items ?? [])
                {
                    var value = FormatDetailFieldValue(item);
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        rows.Add(Text(item.Label, value));
                    }
                }
            }

            if ((detail.Extras?.Systems?.Count ?? 0) > 0)
            {
                rows.Add(Section("星系"));
                rows.Add(Text(string.Join(" / ", detail.Extras!.Systems!)));
            }

            if (detail.Extras?.ReceiveItems is { } receiveItems && receiveItems.ValueKind == JsonValueKind.Array)
            {
                AddMissionItemRows(rows, receiveItems, "奖励物品", ref index);
            }

            if (detail.Extras?.RequiredItems is { } requiredItems && requiredItems.ValueKind == JsonValueKind.Array)
            {
                AddMissionItemRows(rows, requiredItems, "提交需求", ref index);
            }

            if (detail.Extras?.BlueprintRewards is { } blueprintRewards && blueprintRewards.ValueKind == JsonValueKind.Object)
            {
                AddMissionBlueprintRewardRows(rows, blueprintRewards, ref index);
            }

            if (detail.Extras?.Reputation is { } reputation && reputation.ValueKind == JsonValueKind.Object)
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

            if ((detail.Extras?.Systems?.Count ?? 0) > 0)
            {
                parts.Add(string.Join(" / ", detail.Extras!.Systems!));
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
                ShouldShowSecondaryTerminal(primaryLocation, terminalName))
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

        private static bool ShouldShowSecondaryTerminal(string primaryLocation, string terminalName)
        {
            if (string.IsNullOrWhiteSpace(primaryLocation) || string.IsNullOrWhiteSpace(terminalName))
            {
                return false;
            }

            if (string.Equals(terminalName, primaryLocation, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (ContainsCjk(primaryLocation))
            {
                return false;
            }

            var normalizedPrimary = NormalizeLocationCompareText(primaryLocation);
            var normalizedTerminal = NormalizeLocationCompareText(terminalName);
            if (string.IsNullOrWhiteSpace(normalizedPrimary) || string.IsNullOrWhiteSpace(normalizedTerminal))
            {
                return true;
            }

            return !normalizedPrimary.Contains(normalizedTerminal, StringComparison.OrdinalIgnoreCase) &&
                   !normalizedTerminal.Contains(normalizedPrimary, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeLocationCompareText(string text)
            => new(text
                .Where(ch => !char.IsWhiteSpace(ch) && ch is not '-' and not '_' and not '/' and not '(' and not ')' and not '[' and not ']')
                .ToArray());

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

        private IReadOnlyList<PriceRowVm> GetMaterialPreviewBuyRows(MaterialPreviewWindowVm previewWindow)
            => previewWindow.PriceRows.Where(row => row.SideKey == "buy").ToList();

        private IReadOnlyList<PriceRowVm> GetMaterialPreviewSellRows(MaterialPreviewWindowVm previewWindow)
            => previewWindow.PriceRows.Where(row => row.SideKey == "sell").ToList();

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

        private static string ExtractMaterialSearchText(BlueprintMaterialVm material)
        {
            if (string.IsNullOrWhiteSpace(material.Text))
            {
                return string.Empty;
            }

            var separatorIndex = material.Text.IndexOf(" · ", StringComparison.Ordinal);
            return separatorIndex > 0
                ? material.Text[..separatorIndex].Trim()
                : material.Text.Trim();
        }

        private static OverlaySearchItem? PickMaterialPreviewItem(IEnumerable<OverlaySearchItem> items, string searchText)
        {
            var normalizedSearch = NormalizeSearchText(searchText);
            return items
                .OrderByDescending(item => IsExactMaterialMatch(item, normalizedSearch))
                .ThenByDescending(item => string.Equals(item.CategoryKey, "commodities", StringComparison.OrdinalIgnoreCase))
                .ThenBy(item => item.CategoryPriority)
                .ThenByDescending(item => item.Score)
                .FirstOrDefault();
        }

        private static bool IsExactMaterialMatch(OverlaySearchItem item, string normalizedSearch)
        {
            if (string.IsNullOrWhiteSpace(normalizedSearch))
            {
                return false;
            }

            return string.Equals(NormalizeSearchText(item.NameChs), normalizedSearch, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(NormalizeSearchText(item.Name), normalizedSearch, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeSearchText(string? text)
            => string.IsNullOrWhiteSpace(text)
                ? string.Empty
                : new string(text.Where(ch => !char.IsWhiteSpace(ch) && ch != '-' && ch != '_').ToArray()).Trim();

        private void AddMaterialPreviewPriceRows(MaterialPreviewWindowVm previewWindow, string sideKey, IEnumerable<OverlayPriceEntry> entries)
        {
            foreach (var entry in entries.Take(12))
            {
                var location = BuildPreferredPriceLocation(entry);
                previewWindow.PriceRows.Add(new PriceRowVm(
                    $"{previewWindow.Key}:{sideKey}:{previewWindow.PriceRows.Count}",
                    sideKey,
                    location,
                    BuildPriceRowSecondary(entry, location),
                    FormatPrice(entry.Price)));
            }
        }

        private static string BuildMaterialPreviewSubtitle(OverlaySearchItem item)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(item.CategoryLabel))
            {
                parts.Add(item.CategoryLabel!);
            }

            if (!string.IsNullOrWhiteSpace(item.Type))
            {
                parts.Add(item.Type!);
            }

            return string.Join(" · ", parts);
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

        private string BuildSlotSubtitle(BlueprintModifierSlotVm slot)
        {
            var parts = new List<string>();

            parts.Add($"需求 {slot.RequiredCount}");

            if (slot.MinQuality > 0)
            {
                parts.Add($"最低质 {slot.MinQuality}");
            }

            return string.Join(" · ", parts);
        }

        private string BuildModifierCurrentPercentText(BlueprintModifierSlotVm slot, BlueprintModifierValueVm modifier)
        {
            if (!TryComputeModifierFactor(modifier, slot.CurrentQuality, out var factor))
            {
                return "--";
            }

            return FormatPercentDelta(factor);
        }

        private static string BuildModifierRangeText(BlueprintModifierValueVm modifier)
        {
            var parts = new List<string>();

            if (modifier.StartValue is not null && modifier.EndValue is not null)
            {
                parts.Add($"{FormatPercentDelta(modifier.StartValue.Value)} → {FormatPercentDelta(modifier.EndValue.Value)}");
            }

            if (modifier.StartQuality is not null && modifier.EndQuality is not null)
            {
                parts.Add($"质 {FormatNumber(modifier.StartQuality.Value)} → {FormatNumber(modifier.EndQuality.Value)}");
            }

            return string.Join("  ·  ", parts);
        }

        private string BuildModifierPercentStyle(BlueprintModifierSlotVm slot, BlueprintModifierValueVm modifier)
        {
            if (!TryComputeModifierFactor(modifier, slot.CurrentQuality, out var factor))
            {
                return "border-color:rgba(98,129,154,.24);background:rgba(23,35,48,.7);color:#c3d8e8;";
            }

            var delta = (factor - 1m) * 100m;
            if (delta != 0 && IsBeneficialModifierDelta(modifier, delta))
            {
                return "border-color:rgba(60,174,129,.34);background:rgba(16,63,43,.78);color:#7df2ba;";
            }

            if (delta != 0)
            {
                return "border-color:rgba(255,108,137,.28);background:rgba(67,20,30,.72);color:#ff8196;";
            }

            return "border-color:rgba(59,141,117,.28);background:rgba(16,49,40,.72);color:#88f2cb;";
        }

        private static bool IsBeneficialModifierDelta(BlueprintModifierValueVm modifier, decimal delta)
        {
            if (delta == 0)
            {
                return true;
            }

            return IsLowerValueBetter(modifier) ? delta < 0 : delta > 0;
        }

        private static bool IsLowerValueBetter(BlueprintModifierValueVm modifier)
        {
            var probe = $"{modifier.PropertyKey} {modifier.DisplayName}".ToLowerInvariant();
            string[] lowerIsBetterTokens =
            [
                "recoil",
                "kick",
                "spread",
                "bloom",
                "dispersion",
                "sway",
                "cooldown",
                "delay",
                "reload",
                "后坐力",
                "后座力",
                "枪口上跳",
                "上跳",
                "扩散",
                "散布",
                "偏移",
                "冷却",
                "装填",
                "延迟"
            ];

            return lowerIsBetterTokens.Any(probe.Contains);
        }

        private bool HasBuyColumn() => GetBuyRows().Count > 0;
        private bool HasSellColumn() => GetSellRows().Count > 0;
        private bool HasAccessColumn() => _accessRows.Count > 0;
        private bool HasBlueprintColumn() =>
            _blueprintHeader is not null ||
            _blueprintMaterials.Count > 0 ||
            _blueprintBaseStats.Count > 0 ||
            _blueprintModifierSlots.Count > 0;
        private bool HasMissionColumn() => _missionSources.Count > 0;
        private bool HasMiningColumn() => _miningRows.Count > 0;
        private bool HasSelectedMissionDetail() => !string.IsNullOrWhiteSpace(_selectedMissionSourceId);

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
            if (HasBuyColumn()) widths.Add("minmax(0, 0.92fr)");
            if (HasSellColumn()) widths.Add("minmax(0, 0.92fr)");
            if (HasAccessColumn()) widths.Add("minmax(0, 0.90fr)");
            if (HasBlueprintColumn()) widths.Add("minmax(0, 1.18fr)");
            if (HasMissionColumn()) widths.Add("minmax(0, 1.06fr)");
            if (HasMiningColumn()) widths.Add("minmax(0, 1.00fr)");

            return $"height:100%;min-height:0;display:grid;grid-template-columns:{string.Join(" ", widths)};gap:8px;align-items:start;";
        }

        private string BuildMaterialChipStyle(BlueprintMaterialVm material)
            => _materialPreviewWindows.Any(window => string.Equals(window.Key, material.Key, StringComparison.Ordinal))
                ? "padding:3px 7px;border-radius:999px;border:1px solid rgba(91,179,225,.72);background:rgba(18,54,74,.94);color:#f4fbff;font:inherit;font-size:10px;line-height:1.2;cursor:pointer;white-space:nowrap;"
                : string.Empty;

        private static string BuildMissionSourceButtonStyle(bool selected)
            => selected
                ? "width:100%;padding:7px 8px;border:none;border-top:1px solid rgba(63,100,127,.16);background:rgba(20,43,63,.86);box-shadow:inset 2px 0 0 #2ca9e1;color:#fff;text-align:left;cursor:pointer;"
                : "width:100%;padding:7px 8px;border:none;border-top:1px solid rgba(63,100,127,.16);background:transparent;color:#fff;text-align:left;cursor:pointer;";

        private string BuildFloatingWindowStyle(FloatingWindowState state)
            => $"left:{state.Left:0.##}px;top:{state.Top:0.##}px;width:{state.Width}px;z-index:{state.ZIndex};";

        private string BuildMainTerminalShellStyle()
            => $"left:{_mainTerminalWindow.Left:0.##}px;top:{_mainTerminalWindow.Top:0.##}px;z-index:{_mainTerminalWindow.ZIndex};width:min(1600px, calc(100% - 394px));height:calc(100% - 8px);";

        private string BuildMaterialPreviewWindowStyle(MaterialPreviewWindowVm previewWindow) => BuildFloatingWindowStyle(previewWindow.Window);

        private string BuildMissionDetailWindowStyle() => BuildFloatingWindowStyle(_missionDetailWindow);

        private void BringFloatingMaterialPreviewToFront(MaterialPreviewWindowVm previewWindow) => BringFloatingWindowToFront(previewWindow.Window);

        private void BringMissionDetailToFront() => BringFloatingWindowToFront(_missionDetailWindow);

        private FloatingWindowState? GetFloatingWindow(string? windowKey)
        {
            if (string.IsNullOrWhiteSpace(windowKey))
            {
                return null;
            }

            if (string.Equals(windowKey, "main-terminal", StringComparison.Ordinal))
            {
                return _mainTerminalWindow;
            }

            if (string.Equals(windowKey, "mission", StringComparison.Ordinal))
            {
                return _missionDetailWindow;
            }

            if (windowKey.StartsWith("material:", StringComparison.Ordinal))
            {
                var materialKey = windowKey["material:".Length..];
                return _materialPreviewWindows.FirstOrDefault(window =>
                    string.Equals(window.Key, materialKey, StringComparison.Ordinal))?.Window;
            }

            return null;
        }

        private void BringFloatingWindowToFront(FloatingWindowState state)
        {
            if (ReferenceEquals(state, _mainTerminalWindow))
            {
                return;
            }

            state.ZIndex = ++_nextWindowZIndex;
        }

        private bool IsMissionSourceSelected(MissionSourceVm source)
            => !string.IsNullOrWhiteSpace(_selectedMissionSourceId) &&
               string.Equals(_selectedMissionSourceId, source.Id, StringComparison.Ordinal);

        private MissionSectionVm? GetMissionReputationSection()
            => _missionDetailPanel?.ExtraSections.FirstOrDefault(section =>
                string.Equals(section.Key, "mission-reputation", StringComparison.Ordinal));

        private MissionSectionVm? GetMissionVersionSection()
            => _missionDetailPanel?.ExtraSections.FirstOrDefault(section =>
                string.Equals(section.Key, "mission-version-section", StringComparison.Ordinal));

        private IReadOnlyList<MissionSectionVm> GetMissionRemainingExtraSections()
            => _missionDetailPanel?.ExtraSections
                   .Where(section =>
                       !string.Equals(section.Key, "mission-reputation", StringComparison.Ordinal) &&
                       !string.Equals(section.Key, "mission-version-section", StringComparison.Ordinal))
                   .ToList()
               ?? [];

        private static IReadOnlyList<MissionSectionItemVm> GetMissionFilteredExtraItems(MissionSectionVm section)
            => section.Items
                   .Where(item => !IsMissionVersionItem(item))
                   .ToList();

        private static bool IsMissionVersionItem(MissionSectionItemVm item)
            => item.Key.Contains("gameVersion", StringComparison.OrdinalIgnoreCase) ||
               item.Key.Contains("version", StringComparison.OrdinalIgnoreCase) ||
               item.Primary.Contains("版本", StringComparison.CurrentCultureIgnoreCase);

        private static IReadOnlyList<MissionSectionItemVm> GetMissionReputationMetaItems(MissionSectionVm? section)
            => section?.Items
                   .Where(item => !item.Key.StartsWith("mission-reputation-row:", StringComparison.Ordinal))
                   .ToList()
               ?? [];

        private static IReadOnlyList<MissionSectionItemVm> GetMissionReputationRankItems(MissionSectionVm? section)
            => section?.Items
                   .Where(item => item.Key.StartsWith("mission-reputation-row:", StringComparison.Ordinal))
                   .ToList()
               ?? [];

        private static string BuildMissionInlineItemText(MissionSectionItemVm item)
        {
            if (string.IsNullOrWhiteSpace(item.Secondary))
            {
                return item.Primary;
            }

            return item.Secondary.StartsWith("x", StringComparison.OrdinalIgnoreCase)
                ? $"{item.Primary} {item.Secondary}"
                : $"{item.Primary}（{item.Secondary}）";
        }

        private static string BuildMissionReputationRowClass(MissionSectionItemVm item)
        {
            var secondary = item.Secondary ?? string.Empty;

            if (secondary.Contains("最高等级", StringComparison.CurrentCultureIgnoreCase))
            {
                return "mission-rep-row mission-rep-row-max";
            }

            if (secondary.Contains("最低要求", StringComparison.CurrentCultureIgnoreCase) ||
                secondary.Contains("当前等级", StringComparison.CurrentCultureIgnoreCase))
            {
                return "mission-rep-row mission-rep-row-min";
            }

            return "mission-rep-row";
        }

        private static string BuildMissionReputationXpText(MissionSectionItemVm item)
        {
            if (string.IsNullOrWhiteSpace(item.Secondary))
            {
                return "—";
            }

            foreach (var part in item.Secondary.Split('·', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.StartsWith("XP ", StringComparison.OrdinalIgnoreCase))
                {
                    return part["XP ".Length..].Trim();
                }
            }

            return "—";
        }

        private static string BuildMissionReputationMissionCountText(MissionSectionItemVm item)
        {
            if (string.IsNullOrWhiteSpace(item.Secondary))
            {
                return "—";
            }

            foreach (var part in item.Secondary.Split('·', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.StartsWith("任务 ", StringComparison.CurrentCultureIgnoreCase))
                {
                    return part["任务 ".Length..].Trim();
                }
            }

            return "—";
        }

        private static string FormatPercentDelta(decimal factor)
        {
            var percent = (factor - 1m) * 100m;
            var sign = percent > 0 ? "+" : string.Empty;
            return $"{sign}{percent:0.00}%";
        }

        private sealed record PriceRowVm(string Key, string SideKey, string Location, string Detail, string PriceText);
        private sealed record ResultTagVm(string Text, string Style);
        private sealed record BlueprintTagVm(string Text, string Style);
        private sealed record BlueprintHeaderVm(string Title, string? Subtitle, List<BlueprintTagVm> Tags);
        private sealed record BlueprintMaterialVm(string Key, string Text);
        private sealed record BlueprintBaseStatSourceVm(string Key, string PropertyKey, string DisplayName, string DisplayText, decimal? NumericValue);
        private sealed record BlueprintStatVm(string Key, string Name, string ValueText);
        private sealed class MaterialPreviewWindowVm
        {
            public MaterialPreviewWindowVm(string key, string title, FloatingWindowState window)
            {
                Key = key;
                Title = title;
                Window = window;
            }

            public string Key { get; }
            public string Title { get; set; }
            public string Subtitle { get; set; } = string.Empty;
            public string UpdatedAt { get; set; } = string.Empty;
            public string Status { get; set; } = string.Empty;
            public List<string> AccessRows { get; } = [];
            public List<string> MiningRows { get; } = [];
            public List<PriceRowVm> PriceRows { get; } = [];
            public int RequestVersion { get; set; }
            public FloatingWindowState Window { get; }
        }
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
        private sealed record MissionTagVm(string Text, string ToneClass);
        private sealed record MissionStatCardVm(string Title, string Value, string ToneClass);
        private sealed record MissionInfoLineVm(string Key, string Label, string Value);
        private sealed record MissionSectionItemVm(string Key, string Primary, string? Secondary);
        private sealed record MissionSectionVm(string Key, string Title, MissionSectionKind Kind, List<MissionSectionItemVm> Items, string? Summary = null);
        private sealed record MissionDetailPanelVm(
            string? EnglishName,
            string? Overview,
            string VersionTag,
            List<MissionTagVm> HeaderTags,
            List<MissionStatCardVm> StatCards,
            List<MissionInfoLineVm> RequirementLines,
            List<MissionTagVm> RequirementTags,
            List<MissionSectionVm> RewardSections,
            List<MissionSectionVm> RequirementSections,
            List<MissionSectionVm> ExtraSections);
        private sealed record MissionRowVm(string Key, MissionRowKind Kind, string Text, string? SecondaryText = null);

        private sealed class FloatingWindowState
        {
            public FloatingWindowState(double left, double top, double width, int zIndex)
            {
                Left = left;
                Top = top;
                Width = width;
                ZIndex = zIndex;
            }

            public double Left { get; set; }
            public double Top { get; set; }
            public double Width { get; }
            public int ZIndex { get; set; }
        }

        private enum MissionRowKind
        {
            Section,
            Text
        }

        private enum MissionSectionKind
        {
            Info,
            List
        }
    }
}
