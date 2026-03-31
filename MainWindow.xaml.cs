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
        private const int InteractionHotKeyId = 9000;
        private const int VisibilityHotKeyId = 9001;
        private const uint ModAlt = 0x0001;
        private const uint ModShift = 0x0004;
        private const int WmHotKey = 0x0312;
        private const int GwlExStyle = -20;
        private const int WsExTransparent = 0x00000020;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpFrameChanged = 0x0020;
        private const uint MonitorDefaultToNearest = 2;
        private const int GwOwner = 4;
        private static readonly string[] GameProcessNames = ["StarCitizen", "StarCitizen_Launcher"];

        private System.IntPtr _windowHandle;
        private System.Windows.Interop.HwndSource? _hwndSource;
        private readonly System.Windows.Threading.DispatcherTimer _monitorSyncTimer;
        private readonly Forms.NotifyIcon _notifyIcon;
        private readonly Forms.ToolStripMenuItem _toggleOverlayMenuItem;
        private bool _isInteractive = true;
        private bool _isOverlayVisible = true;
        private bool _isExiting;
        private bool _interactionHotKeyRegistered;
        private bool _visibilityHotKeyRegistered;
        private const int MaxDisplayedResults = 8;
        private static readonly HttpClient SearchHttpClient = new();
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };
        private bool _isSearching;
        private string? _apiBaseUrl;

        public ObservableCollection<SearchResultViewModel> SearchResults { get; } = new();

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;
            _monitorSyncTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _monitorSyncTimer.Tick += MonitorSyncTimer_Tick;
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

            _apiBaseUrl = LoadApiBaseUrl();

            UpdateInteractionStatus();
            InitializeSearchUi();
            InitializeCombatLogUi();
            await RefreshCombatLogAsync();
        }

        private void MainWindow_SourceInitialized(object? sender, System.EventArgs e)
        {
            _windowHandle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            _hwndSource = System.Windows.Interop.HwndSource.FromHwnd(_windowHandle);
            _hwndSource?.AddHook(WndProc);

            _interactionHotKeyRegistered = RegisterHotKey(
                _windowHandle,
                InteractionHotKeyId,
                ModAlt | ModShift,
                (uint)KeyInterop.VirtualKeyFromKey(Key.S));
            _visibilityHotKeyRegistered = RegisterHotKey(
                _windowHandle,
                VisibilityHotKeyId,
                ModAlt | ModShift,
                (uint)KeyInterop.VirtualKeyFromKey(Key.O));

            ApplyInteractionMode();
        }

        private void MainWindow_Closed(object? sender, System.EventArgs e)
        {
            _monitorSyncTimer.Stop();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();

            if (_interactionHotKeyRegistered)
            {
                UnregisterHotKey(_windowHandle, InteractionHotKeyId);
            }

            if (_visibilityHotKeyRegistered)
            {
                UnregisterHotKey(_windowHandle, VisibilityHotKeyId);
            }

            _hwndSource?.RemoveHook(WndProc);
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
            Hide();
            _isOverlayVisible = false;
            UpdateTrayMenuText();
        }

        private void RestoreOverlayFromTray()
        {
            UpdateOverlayTargetBounds();
            Show();
            Activate();
            Topmost = true;
            _isOverlayVisible = true;
            ApplyInteractionMode();
            UpdateTrayMenuText();
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

            if (Math.Abs(Left - targetBounds.Left) < 0.1 &&
                Math.Abs(Top - targetBounds.Top) < 0.1 &&
                Math.Abs(Width - targetBounds.Width) < 0.1 &&
                Math.Abs(Height - targetBounds.Height) < 0.1)
            {
                return;
            }

            Left = targetBounds.Left;
            Top = targetBounds.Top;
            Width = targetBounds.Width;
            Height = targetBounds.Height;
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
                InteractionDescriptionText.Text = _interactionHotKeyRegistered
                    ? "当前是全屏透明宿主层。可交互模式下可以点击面板内容。"
                    : "窗口当前可接收鼠标输入，但交互切换热键注册失败。";
            }
            else
            {
                InteractionModeText.Text = "鼠标穿透";
                InteractionDescriptionText.Text = _interactionHotKeyRegistered
                    ? "鼠标点击会直接穿过悬浮层。按 Alt + Shift + S 可以恢复交互。"
                    : "鼠标点击会直接穿过悬浮层，但交互切换热键注册失败。";
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
                UpdateSelectedSearchResult(SearchResults[0]);

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
            UpdateSelectedSearchResult(SearchResults[targetIndex]);
            return true;
        }

        private void SearchResultsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateSelectedSearchResult(SearchResultsListBox.SelectedItem as SearchResultViewModel);
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
            SelectedResultMetaText.Text = result.SummaryMetaLine;
            SelectedResultFlagsText.Text = result.FlagsLine;
            SelectedResultFooterText.Text = result.FooterLine;
        }

        private void ResetSelectedSearchResultSummary()
        {
            SelectedResultCategoryText.Text = "未选中";
            SelectedResultNameText.Text = "还没有选中结果";
            SelectedResultNameChsText.Text = "执行搜索后会自动选中第一条结果。";
            SelectedResultMetaText.Text = "输入关键词后按回车，也可以用上下方向键切换当前项。";
            SelectedResultFlagsText.Text = "特殊标记：暂无";
            SelectedResultFooterText.Text = "-";
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

        private System.IntPtr WndProc(
            System.IntPtr hwnd,
            int msg,
            System.IntPtr wParam,
            System.IntPtr lParam,
            ref bool handled)
        {
            if (msg != WmHotKey)
            {
                return System.IntPtr.Zero;
            }

            if (wParam.ToInt32() == InteractionHotKeyId)
            {
                ToggleInteractionMode();
                handled = true;
            }
            else if (wParam.ToInt32() == VisibilityHotKeyId)
            {
                ToggleOverlayVisibility();
                handled = true;
            }

            return System.IntPtr.Zero;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(System.IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(System.IntPtr hWnd, int id);

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

        private sealed class ItemSearchResponse
        {
            public string Query { get; set; } = string.Empty;

            public int Total { get; set; }

            public List<ItemSearchResult> Results { get; set; } = [];
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
