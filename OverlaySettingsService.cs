using System;
using System.IO;
using System.Text.Json;

namespace StarCitizenOverLay
{
    public enum OverlaySearchModePreference
    {
        Items,
        Missions
    }

    internal sealed class OverlaySettingsSnapshot
    {
        public bool ShowSidebarTitlePanel { get; set; } = true;
        public bool ShowSidebarStatusPanel { get; set; } = true;
        public bool ShowSidebarLogPanel { get; set; } = true;
        public bool ShowMainTerminalPanel { get; set; } = true;
        public OverlaySearchModePreference DefaultSearchMode { get; set; } = OverlaySearchModePreference.Items;
        public string DefaultItemQuery { get; set; } = "P8";

        public OverlaySettingsSnapshot Clone()
        {
            return new OverlaySettingsSnapshot
            {
                ShowSidebarTitlePanel = ShowSidebarTitlePanel,
                ShowSidebarStatusPanel = ShowSidebarStatusPanel,
                ShowSidebarLogPanel = ShowSidebarLogPanel,
                ShowMainTerminalPanel = ShowMainTerminalPanel,
                DefaultSearchMode = DefaultSearchMode,
                DefaultItemQuery = DefaultItemQuery
            };
        }

        public static OverlaySettingsSnapshot CreateDefault() => new();
    }

    public sealed record OverlaySettingsVm(
        bool ShowSidebarTitlePanel,
        bool ShowSidebarStatusPanel,
        bool ShowSidebarLogPanel,
        bool ShowMainTerminalPanel,
        OverlaySearchModePreference DefaultSearchMode,
        string DefaultItemQuery,
        string InteractionToggleHint,
        string VisibilityToggleHint,
        string ExitHint);

    internal sealed class OverlaySettingsService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        private readonly string _settingsPath;

        public OverlaySettingsService()
        {
            _settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "StarCitizenOverLay",
                "settings.json");

            Current = Load();
        }

        public event Action? Changed;

        public OverlaySettingsSnapshot Current { get; private set; }

        public void Update(Action<OverlaySettingsSnapshot> apply)
        {
            var next = Current.Clone();
            apply(next);
            Current = Normalize(next);
            Save();
            Changed?.Invoke();
        }

        public void Reset()
        {
            Current = Normalize(OverlaySettingsSnapshot.CreateDefault());
            Save();
            Changed?.Invoke();
        }

        private OverlaySettingsSnapshot Load()
        {
            try
            {
                if (!File.Exists(_settingsPath))
                {
                    return Normalize(OverlaySettingsSnapshot.CreateDefault());
                }

                var json = File.ReadAllText(_settingsPath);
                var settings = JsonSerializer.Deserialize<OverlaySettingsSnapshot>(json, JsonOptions);
                return Normalize(settings ?? OverlaySettingsSnapshot.CreateDefault());
            }
            catch
            {
                return Normalize(OverlaySettingsSnapshot.CreateDefault());
            }
        }

        private void Save()
        {
            var directory = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(Current, JsonOptions);
            File.WriteAllText(_settingsPath, json);
        }

        private static OverlaySettingsSnapshot Normalize(OverlaySettingsSnapshot settings)
        {
            settings.ShowSidebarStatusPanel = true;
            settings.DefaultItemQuery ??= string.Empty;
            settings.DefaultItemQuery = settings.DefaultItemQuery.Trim();
            return settings;
        }
    }
}
