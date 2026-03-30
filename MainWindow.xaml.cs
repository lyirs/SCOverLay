using System.Text;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
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
        private const int HotKeyId = 9000;
        private const uint ModAlt = 0x0001;
        private const uint ModShift = 0x0004;
        private const int WmHotKey = 0x0312;
        private const int GwlExStyle = -20;
        private const int WsExTransparent = 0x00000020;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpFrameChanged = 0x0020;

        private System.IntPtr _windowHandle;
        private System.Windows.Interop.HwndSource? _hwndSource;
        private bool _isInteractive = true;
        private bool _hotKeyRegistered;
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
            SourceInitialized += MainWindow_SourceInitialized;
            Loaded += MainWindow_Loaded;
            Closed += MainWindow_Closed;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            var workArea = SystemParameters.WorkArea;
            Left = workArea.Right - ActualWidth - OverlayMargin;
            Top = workArea.Top + OverlayMargin;

            _apiBaseUrl = LoadApiBaseUrl();

            UpdateInteractionStatus();
            UpdateConfigurationStatus();
        }

        private void MainWindow_SourceInitialized(object? sender, System.EventArgs e)
        {
            _windowHandle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            _hwndSource = System.Windows.Interop.HwndSource.FromHwnd(_windowHandle);
            _hwndSource?.AddHook(WndProc);

            _hotKeyRegistered = RegisterHotKey(
                _windowHandle,
                HotKeyId,
                ModAlt | ModShift,
                (uint)KeyInterop.VirtualKeyFromKey(Key.S));

            ApplyInteractionMode();
        }

        private void MainWindow_Closed(object? sender, System.EventArgs e)
        {
            if (_hotKeyRegistered)
            {
                UnregisterHotKey(_windowHandle, HotKeyId);
            }

            _hwndSource?.RemoveHook(WndProc);
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

        private async void SearchQueryTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await ExecuteSearchAsync();
            }
        }

        private void ToggleInteractionMode()
        {
            _isInteractive = !_isInteractive;
            ApplyInteractionMode();
            UpdateInteractionStatus();
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
                InteractionModeText.Text = "Interactive";
                InteractionDescriptionText.Text = _hotKeyRegistered
                    ? "Window accepts mouse input. Drag the title area to move the overlay."
                    : "Window accepts mouse input. Hotkey registration failed, so toggle is unavailable.";
            }
            else
            {
                InteractionModeText.Text = "Click-Through";
                InteractionDescriptionText.Text = _hotKeyRegistered
                    ? "Mouse clicks pass through the overlay. Press Alt + Shift + S to interact again."
                    : "Mouse clicks pass through the overlay. Hotkey registration failed.";
            }
        }

        private void UpdateConfigurationStatus()
        {
            var hasConfig = !string.IsNullOrWhiteSpace(_apiBaseUrl);

            ConfigStatusText.Visibility = hasConfig ? Visibility.Collapsed : Visibility.Visible;
            SearchQueryTextBox.IsEnabled = hasConfig && !_isSearching;
            SearchButton.IsEnabled = hasConfig && !_isSearching;
            SearchButton.Content = _isSearching ? "Searching..." : "Search";

            if (hasConfig)
            {
                SearchStatusText.Text = "Ready";
                SearchSummaryText.Text = "No search yet.";
                return;
            }

            ConfigStatusText.Text = "Missing API_BASE_URL in .env. Update the root .env file, rebuild, and run again.";
            SearchStatusText.Text = "Search unavailable.";
            SearchSummaryText.Text = "Configuration required.";
            SearchResults.Clear();
        }

        private async Task ExecuteSearchAsync()
        {
            if (_isSearching)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(_apiBaseUrl))
            {
                UpdateConfigurationStatus();
                return;
            }

            var query = SearchQueryTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(query))
            {
                SearchStatusText.Text = "Enter a keyword before searching.";
                SearchSummaryText.Text = "Waiting for input.";
                SearchResults.Clear();
                return;
            }

            _isSearching = true;
            SearchQueryTextBox.IsEnabled = false;
            SearchButton.IsEnabled = false;
            SearchButton.Content = "Searching...";
            SearchStatusText.Text = $"Searching \"{query}\"...";
            SearchSummaryText.Text = "Waiting for response.";
            SearchResults.Clear();

            try
            {
                var requestUrl = BuildSearchUrl(_apiBaseUrl, query);
                using var response = await SearchHttpClient.GetAsync(requestUrl);

                if (!response.IsSuccessStatusCode)
                {
                    SearchStatusText.Text = $"Request failed: {(int)response.StatusCode} {response.ReasonPhrase}";
                    SearchSummaryText.Text = "Server did not return a successful search result.";
                    return;
                }

                await using var responseStream = await response.Content.ReadAsStreamAsync();
                var payload = await JsonSerializer.DeserializeAsync<ItemSearchResponse>(responseStream, JsonOptions);
                var results = payload?.Results ?? [];

                foreach (var result in results.Take(MaxDisplayedResults))
                {
                    SearchResults.Add(new SearchResultViewModel(result));
                }

                SearchStatusText.Text = "Search complete.";

                if (payload is null || payload.Total == 0 || SearchResults.Count == 0)
                {
                    SearchSummaryText.Text = "No matching items.";
                    return;
                }

                SearchSummaryText.Text = payload.Total > SearchResults.Count
                    ? $"Showing first {SearchResults.Count} of {payload.Total} results."
                    : $"{SearchResults.Count} result(s).";
            }
            catch (Exception ex)
            {
                SearchStatusText.Text = $"Search failed: {ex.Message}";
                SearchSummaryText.Text = "Check API_BASE_URL or server availability.";
            }
            finally
            {
                _isSearching = false;
                UpdateConfigurationStatus();
            }
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
            if (msg == WmHotKey && wParam.ToInt32() == HotKeyId)
            {
                ToggleInteractionMode();
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

        public sealed class SearchResultViewModel
        {
            public SearchResultViewModel(ItemSearchResult result)
            {
                Name = string.IsNullOrWhiteSpace(result.Name) ? "(Unnamed item)" : result.Name;
                NameChsDisplay = string.IsNullOrWhiteSpace(result.NameChs) ? "No Chinese name" : result.NameChs;
                CategoryLabel = string.IsNullOrWhiteSpace(result.CategoryLabel) ? "Unknown" : result.CategoryLabel;

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

                MetaLine = metaParts.Count > 0 ? string.Join(" / ", metaParts) : "No extra metadata";
            }

            public string Name { get; }

            public string NameChsDisplay { get; }

            public string CategoryLabel { get; }

            public string MetaLine { get; }
        }

        private sealed class ItemSearchResponse
        {
            public string Query { get; set; } = string.Empty;

            public int Total { get; set; }

            public List<ItemSearchResult> Results { get; set; } = [];
        }

        public sealed class ItemSearchResult
        {
            public string CategoryLabel { get; set; } = string.Empty;

            public string Name { get; set; } = string.Empty;

            public string NameChs { get; set; } = string.Empty;

            public string? Size { get; set; }

            public string? Type { get; set; }

            public string? Rank { get; set; }
        }
    }
}
