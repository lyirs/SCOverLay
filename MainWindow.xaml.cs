using System.Text;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Forms = System.Windows.Forms;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace StarCitizenOverLay
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private const double OverlayMargin = 24;
        private const int GwlExStyle = -20;
        private const int WsExTransparent = 0x00000020;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpFrameChanged = 0x0020;
        private const uint MonitorDefaultToNearest = 2;
        private const int GwOwner = 4;
        private const double SearchTerminalMinHeight = 680;
        private const double SearchTerminalBottomPadding = 12;
        private static readonly string[] GameProcessNames = ["StarCitizen", "StarCitizen_Launcher"];

        private System.IntPtr _windowHandle;
        private readonly System.Windows.Threading.DispatcherTimer _monitorSyncTimer;
        private readonly GlobalKeyboardShortcutListener _globalKeyboardShortcutListener;
        private readonly Forms.NotifyIcon _notifyIcon;
        private readonly Forms.ToolStripMenuItem _toggleOverlayMenuItem;
        private bool _isInteractive = true;
        private bool _interactionModeBeforeHide = true;
        private bool _isOverlayVisible = true;
        private bool _isExiting;
        private const int MaxDisplayedResults = 8;
        private static readonly HttpClient SearchHttpClient = new();
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };
        private bool _isSearching;
        private int _detailRequestVersion;
        private string? _apiBaseUrl;

        public ObservableCollection<SearchResultViewModel> SearchResults { get; } = new();
        public ObservableCollection<ItemDetailSectionViewModel> SelectedDetailSections { get; } = new();
        public ObservableCollection<string> SelectedDetailTags { get; } = new();
        public ObservableCollection<ItemDetailPriceGroupViewModel> SelectedDetailPriceGroups { get; } = new();

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;
            _monitorSyncTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _monitorSyncTimer.Tick += MonitorSyncTimer_Tick;
            _globalKeyboardShortcutListener = new GlobalKeyboardShortcutListener();
            _globalKeyboardShortcutListener.InteractionHotKeyPressed += GlobalKeyboardShortcutListener_InteractionHotKeyPressed;
            _globalKeyboardShortcutListener.VisibilityHotKeyPressed += GlobalKeyboardShortcutListener_VisibilityHotKeyPressed;
            _toggleOverlayMenuItem = new Forms.ToolStripMenuItem("隐藏 Overlay");
            _toggleOverlayMenuItem.Click += ToggleOverlayMenuItem_Click;

            var exitMenuItem = new Forms.ToolStripMenuItem("退出程序");
            exitMenuItem.Click += ExitMenuItem_Click;

            var trayMenu = new Forms.ContextMenuStrip();
            trayMenu.Items.Add(_toggleOverlayMenuItem);
            trayMenu.Items.Add(new Forms.ToolStripSeparator());
            trayMenu.Items.Add(exitMenuItem);

            _notifyIcon = new Forms.NotifyIcon
            {
                Text = "Star Citizen Overlay",
                Icon = LoadNotifyIcon(),
                ContextMenuStrip = trayMenu,
                Visible = true
            };
            _notifyIcon.DoubleClick += NotifyIcon_DoubleClick;
            SourceInitialized += MainWindow_SourceInitialized;
            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
            Closed += MainWindow_Closed;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateOverlayTargetBounds();
            _monitorSyncTimer.Start();

            _apiBaseUrl = OverlayApiConfiguration.LoadApiBaseUrl();

            UpdateInteractionStatus();
            InitializeSearchUi();
            InitializeCombatLogUi();
            await RefreshCombatLogAsync();
        }

        private void MainWindow_SourceInitialized(object? sender, System.EventArgs e)
        {
            _windowHandle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            _globalKeyboardShortcutListener.Start();
            ApplyInteractionMode();
        }

        private void MainWindow_Closed(object? sender, System.EventArgs e)
        {
            _monitorSyncTimer.Stop();
            _globalKeyboardShortcutListener.Dispose();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_isExiting)
            {
                return;
            }

            e.Cancel = true;
            HideOverlayToTray();
        }

        private void MonitorSyncTimer_Tick(object? sender, EventArgs e)
        {
            if (!_isOverlayVisible)
            {
                return;
            }

            UpdateOverlayTargetBounds();
        }

        private void NotifyIcon_DoubleClick(object? sender, EventArgs e)
        {
            ToggleOverlayVisibility();
        }

        private void SearchBlazorWebView_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is Microsoft.AspNetCore.Components.WebView.Wpf.BlazorWebView blazorWebView)
            {
                blazorWebView.WebView.DefaultBackgroundColor = System.Drawing.Color.Transparent;
                blazorWebView.Focus();
                blazorWebView.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        blazorWebView.WebView.Focus();
                    }
                    catch
                    {
                        // WebView focus can race CoreWebView initialization during startup.
                    }
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        private void GlobalKeyboardShortcutListener_InteractionHotKeyPressed(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(ToggleInteractionMode);
        }

        private void GlobalKeyboardShortcutListener_VisibilityHotKeyPressed(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(ToggleOverlayVisibility);
        }

        private void ToggleOverlayMenuItem_Click(object? sender, EventArgs e)
        {
            ToggleOverlayVisibility();
        }

        private void ExitMenuItem_Click(object? sender, EventArgs e)
        {
            _isExiting = true;
            _notifyIcon.Visible = false;
            Close();
        }

        private void OverlayPanel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_isInteractive && e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private async void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            await ExecuteSearchAsync();
        }

        private async void SearchQueryTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await ExecuteSearchAsync();
                return;
            }

            if (e.Key == Key.Down)
            {
                e.Handled = MoveSearchSelection(1);
                return;
            }

            if (e.Key == Key.Up)
            {
                e.Handled = MoveSearchSelection(-1);
            }
        }

        private void ToggleInteractionMode()
        {
            _isInteractive = !_isInteractive;
            ApplyInteractionMode();
            UpdateInteractionStatus();
        }

        private void ToggleOverlayVisibility()
        {
            if (_isOverlayVisible)
            {
                HideOverlayToTray();
                return;
            }

            RestoreOverlayFromTray();
        }

        private void HideOverlayToTray()
        {
            _interactionModeBeforeHide = _isInteractive;
            _isInteractive = false;
            OverlayContentHost.Visibility = Visibility.Collapsed;
            _isOverlayVisible = false;
            ApplyInteractionMode();
            ReturnFocusToGameWindow();
            UpdateTrayMenuText();
        }

        private void RestoreOverlayFromTray()
        {
            UpdateOverlayTargetBounds();
            OverlayContentHost.Visibility = Visibility.Visible;
            _isOverlayVisible = true;
            _isInteractive = _interactionModeBeforeHide;
            ForceOverlayToFront();
            ApplyInteractionMode();
            UpdateTrayMenuText();
        }

        private void ForceOverlayToFront()
        {
            var foregroundWindow = GetForegroundWindow();
            var foregroundThreadId = foregroundWindow == IntPtr.Zero
                ? 0u
                : GetWindowThreadProcessId(foregroundWindow, out _);
            var currentThreadId = GetCurrentThreadId();
            var attachedToForeground = foregroundThreadId != 0 && foregroundThreadId != currentThreadId;

            if (attachedToForeground)
            {
                AttachThreadInput(foregroundThreadId, currentThreadId, true);
            }

            try
            {
                Topmost = false;
                Topmost = true;
                BringWindowToTop(_windowHandle);
                SetForegroundWindow(_windowHandle);
                Activate();
                Focus();
            }
            finally
            {
                if (attachedToForeground)
                {
                    AttachThreadInput(foregroundThreadId, currentThreadId, false);
                }
            }
        }

        private void ReturnFocusToGameWindow()
        {
            var gameWindowHandle = FindGameWindowHandle();
            if (gameWindowHandle != IntPtr.Zero)
            {
                SetForegroundWindow(gameWindowHandle);
            }
        }

        private void UpdateTrayMenuText()
        {
            _toggleOverlayMenuItem.Text = _isOverlayVisible ? "隐藏 Overlay" : "显示 Overlay";
        }

        private static System.Drawing.Icon LoadNotifyIcon()
        {
            var resourceInfo = System.Windows.Application.GetResourceStream(new Uri("Assets/AppIcon.ico", UriKind.Relative));
            if (resourceInfo is null)
            {
                throw new InvalidOperationException("未找到内置图标资源 Assets/AppIcon.ico。");
            }

            using var iconStream = resourceInfo.Stream;
            using var memoryStream = new MemoryStream();
            iconStream.CopyTo(memoryStream);
            memoryStream.Position = 0;
            using var icon = new System.Drawing.Icon(memoryStream);
            return (System.Drawing.Icon)icon.Clone();
        }

        private void UpdateOverlayTargetBounds()
        {
            var targetBounds = TryGetGameMonitorBounds(out var gameMonitorBounds)
                ? gameMonitorBounds
                : GetPrimaryWorkAreaBounds();

            var boundsUnchanged = Math.Abs(Left - targetBounds.Left) < 0.1 &&
                Math.Abs(Top - targetBounds.Top) < 0.1 &&
                Math.Abs(Width - targetBounds.Width) < 0.1 &&
                Math.Abs(Height - targetBounds.Height) < 0.1;

            if (!boundsUnchanged)
            {
                Left = targetBounds.Left;
                Top = targetBounds.Top;
                Width = targetBounds.Width;
                Height = targetBounds.Height;
            }

            UpdateSearchTerminalHostSize(targetBounds);
        }

        private void UpdateSearchTerminalHostSize(OverlayBounds targetBounds)
        {
            if (SearchTerminalHost is null)
            {
                return;
            }

            var maxAvailableHeight = Math.Max(
                SearchTerminalMinHeight,
                targetBounds.Height - (OverlayMargin * 2) - SearchTerminalBottomPadding);

            SearchTerminalHost.Height = maxAvailableHeight;
        }

        private static OverlayBounds GetPrimaryWorkAreaBounds()
        {
            var workArea = SystemParameters.WorkArea;
            return new OverlayBounds(workArea.Left, workArea.Top, workArea.Width, workArea.Height);
        }

        private bool TryGetGameMonitorBounds(out OverlayBounds bounds)
        {
            bounds = default;

            var gameWindowHandle = FindGameWindowHandle();
            if (gameWindowHandle == System.IntPtr.Zero)
            {
                return false;
            }

            var monitorHandle = MonitorFromWindow(gameWindowHandle, MonitorDefaultToNearest);
            if (monitorHandle == System.IntPtr.Zero)
            {
                return false;
            }

            var monitorInfo = new MonitorInfo();
            monitorInfo.cbSize = Marshal.SizeOf<MonitorInfo>();
            if (!GetMonitorInfo(monitorHandle, ref monitorInfo))
            {
                return false;
            }

            bounds = ConvertMonitorRectToDipBounds(monitorHandle, monitorInfo.rcMonitor);
            return true;
        }

        private static System.IntPtr FindGameWindowHandle()
        {
            var processIds = new HashSet<int>();

            foreach (var processName in GameProcessNames)
            {
                foreach (var process in Process.GetProcessesByName(processName))
                {
                    processIds.Add(process.Id);
                }
            }

            if (processIds.Count == 0)
            {
                return System.IntPtr.Zero;
            }

            var result = System.IntPtr.Zero;
            EnumWindows(
                (windowHandle, _) =>
                {
                    if (!IsWindowVisible(windowHandle) || GetWindow(windowHandle, GwOwner) != System.IntPtr.Zero)
                    {
                        return true;
                    }

                    GetWindowThreadProcessId(windowHandle, out var processId);
                    if (!processIds.Contains(unchecked((int)processId)))
                    {
                        return true;
                    }

                    result = windowHandle;
                    return false;
                },
                System.IntPtr.Zero);

            return result;
        }

        private static OverlayBounds ConvertMonitorRectToDipBounds(System.IntPtr monitorHandle, RectNative monitorRect)
        {
            var dpiScale = GetMonitorScale(monitorHandle);
            return new OverlayBounds(
                monitorRect.Left / dpiScale.X,
                monitorRect.Top / dpiScale.Y,
                (monitorRect.Right - monitorRect.Left) / dpiScale.X,
                (monitorRect.Bottom - monitorRect.Top) / dpiScale.Y);
        }

        private static (double X, double Y) GetMonitorScale(System.IntPtr monitorHandle)
        {
            if (GetDpiForMonitor(monitorHandle, MonitorDpiType.Effective, out var dpiX, out var dpiY) == 0 &&
                dpiX > 0 &&
                dpiY > 0)
            {
                return (dpiX / 96.0, dpiY / 96.0);
            }

            return (1.0, 1.0);
        }

        private void ApplyInteractionMode()
        {
            if (_windowHandle == System.IntPtr.Zero)
            {
                return;
            }

            var extendedStyle = GetWindowLongPtr(_windowHandle, GwlExStyle).ToInt64();

            if (_isInteractive)
            {
                extendedStyle &= ~WsExTransparent;
            }
            else
            {
                extendedStyle |= WsExTransparent;
            }

            SetWindowLongPtr(_windowHandle, GwlExStyle, new System.IntPtr(extendedStyle));
            SetWindowPos(
                _windowHandle,
                System.IntPtr.Zero,
                0,
                0,
                0,
                0,
                SwpNoMove | SwpNoSize | SwpNoZOrder | SwpFrameChanged);
        }

        private void UpdateInteractionStatus()
        {
            if (_isInteractive)
            {
                InteractionModeText.Text = "可交互";
                InteractionDescriptionText.Text = "当前是全屏透明宿主层。可交互模式下可以点击面板内容。";
            }
            else
            {
                InteractionModeText.Text = "鼠标穿透";
                InteractionDescriptionText.Text = "鼠标点击会直接穿过悬浮层。按小键盘 1 可以恢复交互。";
            }
        }

        private void InitializeSearchUi()
        {
            UpdateSearchInputState();

            if (HasSearchConfiguration())
            {
                SearchStatusText.Text = "就绪";
                SearchSummaryText.Text = "输入关键词后按回车，也可以用上下方向键切换当前项。";
                ResetSelectedSearchResultSummary();
                return;
            }

            ApplyMissingSearchConfigurationState();
        }

        private void UpdateSearchInputState()
        {
            var hasConfig = HasSearchConfiguration();

            ConfigStatusText.Visibility = hasConfig ? Visibility.Collapsed : Visibility.Visible;
            SearchQueryTextBox.IsEnabled = hasConfig && !_isSearching;
            SearchButton.IsEnabled = hasConfig && !_isSearching;
            SearchButton.Content = _isSearching ? "搜索中..." : "搜索";
        }

        private void ApplyMissingSearchConfigurationState()
        {
            ConfigStatusText.Text = "缺少 .env 中的 API_BASE_URL。请更新项目根目录 .env 后重新生成并运行。";
            SearchStatusText.Text = "当前无法搜索。";
            SearchSummaryText.Text = "需要先完成配置。";
            SearchResults.Clear();
            SearchResultsListBox.SelectedIndex = -1;
            ResetSelectedSearchResultSummary();
        }

        private bool HasSearchConfiguration()
        {
            return !string.IsNullOrWhiteSpace(_apiBaseUrl);
        }

        private async Task ExecuteSearchAsync()
        {
            if (_isSearching)
            {
                return;
            }

            if (!HasSearchConfiguration())
            {
                ApplyMissingSearchConfigurationState();
                return;
            }

            var apiBaseUrl = _apiBaseUrl;
            if (string.IsNullOrWhiteSpace(apiBaseUrl))
            {
                ApplyMissingSearchConfigurationState();
                return;
            }

            var query = SearchQueryTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(query))
            {
                SearchStatusText.Text = "请先输入搜索关键词。";
                SearchSummaryText.Text = "等待输入。";
                SearchResults.Clear();
                SearchResultsListBox.SelectedIndex = -1;
                ResetSelectedSearchResultSummary();
                return;
            }

            _isSearching = true;
            UpdateSearchInputState();
            SearchStatusText.Text = $"正在搜索“{query}”...";
            SearchSummaryText.Text = "等待服务响应。";
            SearchResults.Clear();
            SearchResultsListBox.SelectedIndex = -1;
            ResetSelectedSearchResultSummary();

            try
            {
                var requestUrl = BuildSearchUrl(apiBaseUrl, query);
                using var response = await SearchHttpClient.GetAsync(requestUrl);

                if (!response.IsSuccessStatusCode)
                {
                    SearchStatusText.Text = $"请求失败：{(int)response.StatusCode} {response.ReasonPhrase}";
                    SearchSummaryText.Text = "服务端没有返回成功的搜索结果。";
                    return;
                }

                await using var responseStream = await response.Content.ReadAsStreamAsync();
                var payload = await JsonSerializer.DeserializeAsync<ItemSearchResponse>(responseStream, JsonOptions);
                var results = payload?.Results ?? [];

                foreach (var result in results.Take(MaxDisplayedResults))
                {
                    SearchResults.Add(new SearchResultViewModel(result));
                }

                SearchStatusText.Text = "搜索完成。";

                if (payload is null || payload.Total == 0 || SearchResults.Count == 0)
                {
                    SearchSummaryText.Text = "没有找到匹配物品。";
                    return;
                }

                SearchResultsListBox.SelectedIndex = 0;
                SearchResultsListBox.ScrollIntoView(SearchResults[0]);

                SearchSummaryText.Text = payload.Total > SearchResults.Count
                    ? $"当前显示前 {SearchResults.Count} 条，共 {payload.Total} 条结果。"
                    : $"共找到 {SearchResults.Count} 条结果。";
            }
            catch (Exception ex)
            {
                SearchStatusText.Text = $"搜索失败：{ex.Message}";
                SearchSummaryText.Text = "请检查 API_BASE_URL 或服务是否可访问。";
            }
            finally
            {
                _isSearching = false;
                UpdateSearchInputState();
            }
        }

        private bool MoveSearchSelection(int offset)
        {
            if (SearchResults.Count == 0)
            {
                return false;
            }

            var currentIndex = SearchResultsListBox.SelectedIndex;
            if (currentIndex < 0)
            {
                currentIndex = offset > 0 ? -1 : SearchResults.Count;
            }

            var targetIndex = Math.Clamp(currentIndex + offset, 0, SearchResults.Count - 1);
            if (targetIndex == SearchResultsListBox.SelectedIndex)
            {
                return true;
            }

            SearchResultsListBox.SelectedIndex = targetIndex;
            SearchResultsListBox.ScrollIntoView(SearchResults[targetIndex]);
            return true;
        }

        private async void SearchResultsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var selectedResult = SearchResultsListBox.SelectedItem as SearchResultViewModel;
            UpdateSelectedSearchResult(selectedResult);
            await LoadSelectedResultDetailAsync(selectedResult);
        }

        private void UpdateSelectedSearchResult(SearchResultViewModel? result)
        {
            if (result is null)
            {
                ResetSelectedSearchResultSummary();
                return;
            }

            SelectedResultCategoryText.Text = result.CategoryLabel;
            SelectedResultNameText.Text = result.PrimaryName;
            SelectedResultNameChsText.Text = result.DetailSubtitle;
            SelectedDetailStatusText.Text = "正在加载详情...";
            SelectedDetailVersionText.Text = "详情加载中";
        }

        private void ResetSelectedSearchResultSummary()
        {
            SelectedResultCategoryText.Text = "未选中";
            SelectedResultNameText.Text = "还没有选中结果";
            SelectedResultNameChsText.Text = "执行搜索后会自动选中第一条结果。";
            ResetSelectedDetailContent("选择左侧结果后加载详情。", "等待详情");
        }

        private async Task LoadSelectedResultDetailAsync(SearchResultViewModel? result)
        {
            if (result is null)
            {
                return;
            }

            if (!HasSearchConfiguration())
            {
                ResetSelectedDetailContent("缺少 API 配置，无法加载详情。", "缺少配置");
                return;
            }

            var apiBaseUrl = _apiBaseUrl;
            if (string.IsNullOrWhiteSpace(apiBaseUrl))
            {
                ResetSelectedDetailContent("缺少 API 配置，无法加载详情。", "缺少配置");
                return;
            }

            var requestVersion = ++_detailRequestVersion;
            ResetSelectedDetailContent("正在从接口加载详细信息...", "详情加载中");

            try
            {
                var detailRequestUrl = BuildDetailUrl(apiBaseUrl, result.CategoryKey, result.Id);
                using var detailResponse = await SearchHttpClient.GetAsync(detailRequestUrl);

                if (requestVersion != _detailRequestVersion)
                {
                    return;
                }

                if (!detailResponse.IsSuccessStatusCode)
                {
                    ResetSelectedDetailContent(
                        $"详情请求失败：{(int)detailResponse.StatusCode} {detailResponse.ReasonPhrase}",
                        "详情不可用");
                    return;
                }

                await using var detailStream = await detailResponse.Content.ReadAsStreamAsync();
                var detail = await JsonSerializer.DeserializeAsync<ItemDetailResponse>(detailStream, JsonOptions);

                if (requestVersion != _detailRequestVersion)
                {
                    return;
                }

                if (detail is null)
                {
                    ResetSelectedDetailContent("服务返回了空详情。", "详情为空");
                    return;
                }

                ItemPriceResponse? price = null;
                string? priceError = null;

                try
                {
                    var priceRequestUrl = BuildPriceUrl(apiBaseUrl, result.CategoryKey, result.Id);
                    using var priceResponse = await SearchHttpClient.GetAsync(priceRequestUrl);

                    if (requestVersion != _detailRequestVersion)
                    {
                        return;
                    }

                    if (priceResponse.IsSuccessStatusCode)
                    {
                        await using var priceStream = await priceResponse.Content.ReadAsStreamAsync();
                        price = await JsonSerializer.DeserializeAsync<ItemPriceResponse>(priceStream, JsonOptions);
                    }
                    else
                    {
                        priceError = $"价格请求失败：{(int)priceResponse.StatusCode} {priceResponse.ReasonPhrase}";
                    }
                }
                catch (Exception ex)
                {
                    if (requestVersion != _detailRequestVersion)
                    {
                        return;
                    }

                    priceError = $"价格加载失败：{ex.Message}";
                }

                ApplySelectedDetail(detail, result, price, priceError);
            }
            catch (Exception ex)
            {
                if (requestVersion != _detailRequestVersion)
                {
                    return;
                }

                ResetSelectedDetailContent($"详情加载失败：{ex.Message}", "详情失败");
            }
        }

        private void ApplySelectedDetail(
            ItemDetailResponse detail,
            SearchResultViewModel searchResult,
            ItemPriceResponse? price,
            string? priceError)
        {
            SelectedResultCategoryText.Text = string.IsNullOrWhiteSpace(detail.CategoryLabel)
                ? searchResult.CategoryLabel
                : detail.CategoryLabel;
            SelectedResultNameText.Text = !string.IsNullOrWhiteSpace(detail.NameChs)
                ? detail.NameChs
                : searchResult.PrimaryName;
            SelectedResultNameChsText.Text = BuildDetailSubtitle(detail, searchResult);
            SelectedDetailStatusText.Text = detail.Sections.Count > 0
                ? $"已加载 {detail.Sections.Count} 个详情分组。"
                : "详情已加载，但当前没有可展示的详情分组。";
            SelectedDetailVersionText.Text = !string.IsNullOrWhiteSpace(detail.Extras.Update)
                ? $"更新 {detail.Extras.Update}"
                : $"来源 {detail.SourceType}";

            SelectedDetailSections.Clear();
            foreach (var section in detail.Sections)
            {
                var items = new List<ItemDetailFieldViewModel>();

                foreach (var item in section.Items)
                {
                    var valueText = FormatDetailItemValue(item);
                    if (string.IsNullOrWhiteSpace(item.Label) || string.IsNullOrWhiteSpace(valueText))
                    {
                        continue;
                    }

                    items.Add(new ItemDetailFieldViewModel(item.Label, valueText));
                }

                if (items.Count > 0)
                {
                    SelectedDetailSections.Add(new ItemDetailSectionViewModel(
                        string.IsNullOrWhiteSpace(section.Title) ? "未命名分组" : section.Title,
                        items));
                }
            }

            SelectedDetailTags.Clear();
            foreach (var tag in BuildDetailTags(detail.Flags))
            {
                SelectedDetailTags.Add(tag);
            }

            SelectedDetailAccessText.Text = BuildExtrasSummary(detail.Extras);

            ApplyPriceDetail(price, priceError);

            UpdateSelectedDetailEmptyStates();
        }

        private void ResetSelectedDetailContent(string statusText, string versionText)
        {
            SelectedDetailVersionText.Text = versionText;
            SelectedDetailStatusText.Text = statusText;
            SelectedDetailSections.Clear();
            SelectedDetailTags.Clear();
            SelectedDetailPriceGroups.Clear();
            SelectedDetailAccessText.Text = "暂无获取提示。";
            SelectedDetailPriceSummaryText.Text = "暂无价格信息。";
            UpdateSelectedDetailEmptyStates();
        }

        private void UpdateSelectedDetailEmptyStates()
        {
            SelectedDetailFieldsEmptyText.Visibility = SelectedDetailSections.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SelectedDetailTagsEmptyText.Visibility = SelectedDetailTags.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SelectedDetailPriceEmptyText.Visibility = SelectedDetailPriceGroups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private static string BuildDetailUrl(string apiBaseUrl, string categoryKey, int id)
        {
            return $"{apiBaseUrl.TrimEnd('/')}/api/items/detail?categoryKey={Uri.EscapeDataString(categoryKey)}&id={id}";
        }

        private static string BuildPriceUrl(string apiBaseUrl, string categoryKey, int id)
        {
            return $"{apiBaseUrl.TrimEnd('/')}/api/items/price?categoryKey={Uri.EscapeDataString(categoryKey)}&id={id}";
        }

        private static string BuildDetailSubtitle(ItemDetailResponse detail, SearchResultViewModel searchResult)
        {
            var lines = new List<string>();

            if (!string.IsNullOrWhiteSpace(detail.Name))
            {
                lines.Add(detail.Name);
            }
            else if (!string.IsNullOrWhiteSpace(searchResult.DetailSubtitle))
            {
                lines.Add(searchResult.DetailSubtitle);
            }

            var manufacturerParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(detail.ManufacturerNameChs))
            {
                manufacturerParts.Add(detail.ManufacturerNameChs);
            }

            if (!string.IsNullOrWhiteSpace(detail.ManufacturerName))
            {
                manufacturerParts.Add(detail.ManufacturerName);
            }

            if (manufacturerParts.Count > 0)
            {
                lines.Add($"制造商：{string.Join(" / ", manufacturerParts.Distinct())}");
            }

            return lines.Count > 0 ? string.Join(Environment.NewLine, lines) : "暂无更多说明。";
        }

        private static string FormatDetailItemValue(ItemDetailSectionItem item)
        {
            var rawValue = FormatJsonValue(item.Value);
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return string.Empty;
            }

            return string.IsNullOrWhiteSpace(item.Unit)
                ? rawValue
                : $"{rawValue} {item.Unit}";
        }

        private static string FormatJsonValue(JsonElement value)
        {
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? string.Empty,
                JsonValueKind.Number => value.ToString(),
                JsonValueKind.True => "是",
                JsonValueKind.False => "否",
                JsonValueKind.Array => string.Join(" / ", value.EnumerateArray().Select(FormatJsonValue).Where(item => !string.IsNullOrWhiteSpace(item))),
                JsonValueKind.Object => value.ToString(),
                JsonValueKind.Null => string.Empty,
                JsonValueKind.Undefined => string.Empty,
                _ => value.ToString()
            };
        }

        private static List<string> BuildDetailTags(ItemSearchResultFlags? flags)
        {
            var labels = new List<string>();
            if (flags is null)
            {
                return labels;
            }

            AddFlagLabel(labels, flags.ApiPrice, "API 价格");
            AddFlagLabel(labels, flags.ManualPrice, "手工价格");
            AddFlagLabel(labels, flags.Pledge, "商店物品");
            AddFlagLabel(labels, flags.Subscriber, "订阅物品");
            AddFlagLabel(labels, flags.Concierge, "礼宾物品");
            AddFlagLabel(labels, flags.Limited, "限时销售");
            AddFlagLabel(labels, flags.EventAward, "活动奖励");
            AddFlagLabel(labels, flags.Lucky, "稀有掉落");
            AddFlagLabel(labels, flags.Banu, "巴努兑换");
            return labels;
        }

        private static void AddFlagLabel(ICollection<string> labels, bool isEnabled, string label)
        {
            if (isEnabled)
            {
                labels.Add(label);
            }
        }

        private static string BuildExtrasSummary(ItemDetailExtras extras)
        {
            var lines = new List<string>();

            if (!string.IsNullOrWhiteSpace(extras.AccessAdvice))
            {
                lines.Add($"获取建议：{extras.AccessAdvice}");
            }

            if (!string.IsNullOrWhiteSpace(extras.Update))
            {
                lines.Add($"数据更新：{extras.Update}");
            }

            if (extras.Blueprint.ValueKind != JsonValueKind.Null &&
                extras.Blueprint.ValueKind != JsonValueKind.Undefined)
            {
                lines.Add("蓝图：已提供");
            }

            if (extras.Armorset.ValueKind != JsonValueKind.Null &&
                extras.Armorset.ValueKind != JsonValueKind.Undefined)
            {
                lines.Add("套装：已提供");
            }

            if (extras.MiningSources.ValueKind == JsonValueKind.Array)
            {
                var count = extras.MiningSources.EnumerateArray().Count();
                if (count > 0)
                {
                    lines.Add($"采矿来源：{count} 项");
                }
            }

            return lines.Count > 0 ? string.Join(Environment.NewLine, lines) : "暂无获取提示。";
        }

        private void ApplyPriceDetail(ItemPriceResponse? price, string? priceError)
        {
            SelectedDetailPriceGroups.Clear();

            if (price is null)
            {
                SelectedDetailPriceSummaryText.Text = string.IsNullOrWhiteSpace(priceError)
                    ? "暂无价格信息。"
                    : priceError;
                return;
            }

            SelectedDetailPriceSummaryText.Text = BuildPriceSummary(price.Summary);

            AddPriceEntries("买入", price.Buy, 2);
            AddPriceEntries("卖出", price.Sell, 2);
            AddPriceEntries("租赁", price.Rent, 2);
        }

        private void AddPriceEntries(string label, IEnumerable<ItemPriceEntry> entries, int maxCount)
        {
            foreach (var entry in entries.Take(maxCount))
            {
                var locationText = !string.IsNullOrWhiteSpace(entry.Location)
                    ? entry.Location
                    : !string.IsNullOrWhiteSpace(entry.TerminalName)
                        ? entry.TerminalName
                        : "暂无地点信息";

                if (entry.DurationDays.HasValue)
                {
                    locationText = $"{locationText} / {entry.DurationDays.Value} 天";
                }

                SelectedDetailPriceGroups.Add(new ItemDetailPriceGroupViewModel(
                    $"{label} {FormatPrice(entry.Price)}",
                    string.IsNullOrWhiteSpace(entry.GameVersion) ? "未知版本" : entry.GameVersion,
                    locationText));
            }
        }

        private static string BuildPriceSummary(ItemPriceSummary summary)
        {
            var parts = new List<string>();

            if (summary.BuyCount > 0)
            {
                parts.Add($"买入 {summary.BuyCount} 条 {FormatPriceRange(summary.MinBuyPrice, summary.MaxBuyPrice)}");
            }

            if (summary.SellCount > 0)
            {
                parts.Add($"卖出 {summary.SellCount} 条 {FormatPriceRange(summary.MinSellPrice, summary.MaxSellPrice)}");
            }

            if (summary.RentCount > 0)
            {
                parts.Add($"租赁 {summary.RentCount} 条 {FormatPriceRange(summary.MinRentPrice, summary.MaxRentPrice)}");
            }

            return parts.Count > 0 ? string.Join(Environment.NewLine, parts) : "暂无价格信息。";
        }

        private static string FormatPriceRange(int? minPrice, int? maxPrice)
        {
            if (!minPrice.HasValue && !maxPrice.HasValue)
            {
                return string.Empty;
            }

            if (minPrice == maxPrice)
            {
                return $"({FormatPrice(minPrice)})";
            }

            return $"({FormatPrice(minPrice)} - {FormatPrice(maxPrice)})";
        }

        private static string FormatPrice(int? price)
        {
            return price.HasValue ? $"{price.Value} aUEC" : "-";
        }

        private static string? LoadApiBaseUrl()
        {
            var envPath = System.IO.Path.Combine(AppContext.BaseDirectory, ".env");
            if (!File.Exists(envPath))
            {
                return null;
            }

            foreach (var rawLine in File.ReadAllLines(envPath))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                var separatorIndex = line.IndexOf('=');
                if (separatorIndex <= 0)
                {
                    continue;
                }

                var key = line[..separatorIndex].Trim();
                var value = line[(separatorIndex + 1)..].Trim().Trim('"');

                if (!key.Equals("API_BASE_URL", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return Uri.TryCreate(value, UriKind.Absolute, out _) ? value : null;
            }

            return null;
        }

        private static string BuildSearchUrl(string apiBaseUrl, string query)
        {
            return $"{apiBaseUrl.TrimEnd('/')}/api/items/search?q={Uri.EscapeDataString(query)}";
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern System.IntPtr GetWindowLongPtr(System.IntPtr hWnd, int nIndex);

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern System.IntPtr SetWindowLongPtr(System.IntPtr hWnd, int nIndex, System.IntPtr dwNewLong);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(
            System.IntPtr hWnd,
            System.IntPtr hWndInsertAfter,
            int x,
            int y,
            int cx,
            int cy,
            uint uFlags);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern System.IntPtr MonitorFromWindow(System.IntPtr hWnd, uint dwFlags);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetMonitorInfo(System.IntPtr hMonitor, ref MonitorInfo lpmi);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, System.IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(System.IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern System.IntPtr GetWindow(System.IntPtr hWnd, int uCmd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(System.IntPtr hWnd, out uint lpdwProcessId);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern System.IntPtr GetForegroundWindow();

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool BringWindowToTop(System.IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetForegroundWindow(System.IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(System.IntPtr hmonitor, MonitorDpiType dpiType, out uint dpiX, out uint dpiY);

        private delegate bool EnumWindowsProc(System.IntPtr hWnd, System.IntPtr lParam);

        private readonly record struct OverlayBounds(double Left, double Top, double Width, double Height);

        private enum MonitorDpiType
        {
            Effective = 0
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RectNative
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MonitorInfo
        {
            public int cbSize;
            public RectNative rcMonitor;
            public RectNative rcWork;
            public uint dwFlags;
        }

        public sealed class SearchResultViewModel
        {
            public SearchResultViewModel(ItemSearchResult result)
            {
                Id = result.Id;
                CategoryKey = result.CategoryKey;
                Name = string.IsNullOrWhiteSpace(result.Name) ? "（未命名物品）" : result.Name.Trim();
                var hasChineseName = !string.IsNullOrWhiteSpace(result.NameChs);
                NameChsDisplay = hasChineseName ? result.NameChs!.Trim() : "暂无中文名";
                CategoryLabel = string.IsNullOrWhiteSpace(result.CategoryLabel) ? "未知分类" : result.CategoryLabel;
                PrimaryName = hasChineseName ? NameChsDisplay : Name;
                SecondaryInfoLine = BuildSecondaryInfoLine(hasChineseName ? Name : null, result);
                DetailSubtitle = hasChineseName
                    ? Name
                    : "当前结果暂无中文名，已优先显示英文名。";
                SummaryMetaLine = BuildSummaryMetaLine(result);
                FlagsLine = BuildFlagsLine(result.Flags);
                FooterLine = BuildFooterLine(result);

                var metaParts = new List<string>();
                if (!string.IsNullOrWhiteSpace(result.Size))
                {
                    metaParts.Add(result.Size);
                }

                if (!string.IsNullOrWhiteSpace(result.Type))
                {
                    metaParts.Add(result.Type);
                }

                if (!string.IsNullOrWhiteSpace(result.Rank))
                {
                    metaParts.Add(result.Rank);
                }

                MetaLine = metaParts.Count > 0 ? string.Join(" / ", metaParts) : "暂无额外属性";
            }

            public string Name { get; }

            public int Id { get; }

            public string CategoryKey { get; }

            public string NameChsDisplay { get; }

            public string CategoryLabel { get; }

            public string PrimaryName { get; }

            public string SecondaryInfoLine { get; }

            public string DetailSubtitle { get; }

            public string MetaLine { get; }

            public string SummaryMetaLine { get; }

            public string FlagsLine { get; }

            public string FooterLine { get; }

            private static string BuildSummaryMetaLine(ItemSearchResult result)
            {
                var parts = new List<string>
                {
                    $"分类：{(string.IsNullOrWhiteSpace(result.CategoryLabel) ? "未知分类" : result.CategoryLabel)}"
                };

                if (!string.IsNullOrWhiteSpace(result.Size))
                {
                    parts.Add($"尺寸：{result.Size}");
                }

                if (!string.IsNullOrWhiteSpace(result.Type))
                {
                    parts.Add($"类型：{result.Type}");
                }

                if (!string.IsNullOrWhiteSpace(result.Rank))
                {
                    parts.Add($"等级：{result.Rank}");
                }

                if (result.Rarity.HasValue)
                {
                    parts.Add($"稀有度：{result.Rarity.Value}");
                }

                return string.Join("  ·  ", parts);
            }

            private static string BuildSecondaryInfoLine(string? englishName, ItemSearchResult result)
            {
                var parts = new List<string>();

                if (!string.IsNullOrWhiteSpace(englishName))
                {
                    parts.Add(englishName);
                }

                if (!string.IsNullOrWhiteSpace(result.Size))
                {
                    parts.Add(result.Size.Trim());
                }

                if (!string.IsNullOrWhiteSpace(result.Type))
                {
                    parts.Add(result.Type.Trim());
                }

                if (!string.IsNullOrWhiteSpace(result.Rank))
                {
                    parts.Add(result.Rank.Trim());
                }

                return string.Join("  |  ", parts);
            }

            private static string BuildFlagsLine(ItemSearchResultFlags? flags)
            {
                if (flags is null)
                {
                    return "特殊标记：无";
                }

                var labels = new List<string>();
                AddFlagLabel(labels, flags.ApiPrice, "API 价格");
                AddFlagLabel(labels, flags.ManualPrice, "手工价格");
                AddFlagLabel(labels, flags.Pledge, "商店物品");
                AddFlagLabel(labels, flags.Subscriber, "订阅物品");
                AddFlagLabel(labels, flags.Concierge, "礼宾物品");
                AddFlagLabel(labels, flags.Limited, "限时销售");
                AddFlagLabel(labels, flags.EventAward, "活动奖励");
                AddFlagLabel(labels, flags.Lucky, "稀有掉落");
                AddFlagLabel(labels, flags.Banu, "巴努兑换");

                return labels.Count > 0
                    ? $"特殊标记：{string.Join(" / ", labels)}"
                    : "特殊标记：无";
            }

            private static string BuildFooterLine(ItemSearchResult result)
            {
                var parts = new List<string>();

                if (result.Id > 0)
                {
                    parts.Add($"ID：{result.Id}");
                }

                if (!string.IsNullOrWhiteSpace(result.CategoryKey))
                {
                    parts.Add($"分类键：{result.CategoryKey}");
                }

                if (result.Score != 0)
                {
                    parts.Add($"评分：{result.Score}");
                }

                return parts.Count > 0 ? string.Join("  ·  ", parts) : "-";
            }

            private static void AddFlagLabel(ICollection<string> labels, bool isEnabled, string label)
            {
                if (isEnabled)
                {
                    labels.Add(label);
                }
            }
        }

        public sealed class ItemDetailFieldViewModel
        {
            public ItemDetailFieldViewModel(string label, string value)
            {
                Label = label;
                Value = value;
            }

            public string Label { get; }

            public string Value { get; }
        }

        public sealed class ItemDetailSectionViewModel
        {
            public ItemDetailSectionViewModel(string title, IEnumerable<ItemDetailFieldViewModel> items)
            {
                Title = title;
                Items = items.ToList();
            }

            public string Title { get; }

            public List<ItemDetailFieldViewModel> Items { get; }
        }

        public sealed class ItemDetailPriceGroupViewModel
        {
            public ItemDetailPriceGroupViewModel(string priceText, string versionText, string locationsText)
            {
                PriceText = priceText;
                VersionText = versionText;
                LocationsText = locationsText;
            }

            public string PriceText { get; }

            public string VersionText { get; }

            public string LocationsText { get; }
        }

        private sealed class ItemSearchResponse
        {
            public string Query { get; set; } = string.Empty;

            public int Total { get; set; }

            public List<ItemSearchResult> Results { get; set; } = [];
        }

        private sealed class ItemDetailResponse
        {
            public string CategoryKey { get; set; } = string.Empty;

            public string CategoryLabel { get; set; } = string.Empty;

            public string SourceType { get; set; } = string.Empty;

            public int Id { get; set; }

            public string Name { get; set; } = string.Empty;

            public string NameChs { get; set; } = string.Empty;

            public string? ManufacturerName { get; set; }

            public string? ManufacturerNameChs { get; set; }

            public ItemSearchResultFlags Flags { get; set; } = new();

            public List<ItemDetailSection> Sections { get; set; } = [];

            public ItemDetailExtras Extras { get; set; } = new();
        }

        private sealed class ItemDetailSection
        {
            public string Key { get; set; } = string.Empty;

            public string Title { get; set; } = string.Empty;

            public List<ItemDetailSectionItem> Items { get; set; } = [];
        }

        private sealed class ItemDetailSectionItem
        {
            public string Key { get; set; } = string.Empty;

            public string Label { get; set; } = string.Empty;

            public JsonElement Value { get; set; }

            public string? Unit { get; set; }
        }

        private sealed class ItemDetailExtras
        {
            public string? AccessAdvice { get; set; }

            public string? Update { get; set; }

            public JsonElement Armorset { get; set; }

            public JsonElement Blueprint { get; set; }

            public JsonElement MiningSources { get; set; }
        }

        private sealed class ItemPriceResponse
        {
            public string CategoryKey { get; set; } = string.Empty;

            public string CategoryLabel { get; set; } = string.Empty;

            public string SourceType { get; set; } = string.Empty;

            public int Id { get; set; }

            public string Name { get; set; } = string.Empty;

            public string NameChs { get; set; } = string.Empty;

            public ItemPriceSummary Summary { get; set; } = new();

            public List<ItemPriceEntry> Buy { get; set; } = [];

            public List<ItemPriceEntry> Sell { get; set; } = [];

            public List<ItemPriceEntry> Rent { get; set; } = [];

            public ItemPriceExtras Extras { get; set; } = new();
        }

        private sealed class ItemPriceSummary
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

        private sealed class ItemPriceEntry
        {
            public int? Price { get; set; }

            public string? Location { get; set; }

            public int? TerminalId { get; set; }

            public string? TerminalName { get; set; }

            public string? GameVersion { get; set; }

            public int? DurationDays { get; set; }
        }

        private sealed class ItemPriceExtras
        {
            public long? UpdatedAt { get; set; }

            public string? ClaimTime { get; set; }

            public string? ExpediteTime { get; set; }

            public int? ExpediteFee { get; set; }

            public string? RentAt { get; set; }
        }

        public sealed class ItemSearchResult
        {
            public string CategoryKey { get; set; } = string.Empty;

            public string CategoryLabel { get; set; } = string.Empty;

            public int Id { get; set; }

            public string Name { get; set; } = string.Empty;

            public string NameChs { get; set; } = string.Empty;

            public string? Size { get; set; }

            public string? Type { get; set; }

            public string? Rank { get; set; }

            public int? Rarity { get; set; }

            public int Score { get; set; }

            public ItemSearchResultFlags Flags { get; set; } = new();
        }

        public sealed class ItemSearchResultFlags
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
    }
}
