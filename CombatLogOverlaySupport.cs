using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;

namespace StarCitizenOverLay
{
    public partial class MainWindow
    {
        private const int MaxCombatLogEntries = 12;
        private const int MaxBackupLogsToScan = 5;

        private static readonly Regex LauncherPathRegex = new(
            @"Launching Star Citizen (?<channel>[A-Z\-]+) from \((?<path>.+?)\)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ActorDeathRegex = new(
            @"^<(?<timestamp>[^>]+)>.*?<Actor Death> CActor::Kill: '(?<victim>[^']+)' \[(?<victimId>\d+)\] in zone '(?<zone>[^']*)' killed by '(?<killer>[^']+)' \[(?<killerId>\d+)\] using '(?<weapon>[^']*)' \[Class (?<weaponClass>[^\]]+)\] with damage type '(?<damageType>[^']+)'",
            RegexOptions.Compiled);
        private static readonly Regex PlayerBracketRegex = new(
            @"Player\[(?<name>[^\]]+)\]",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex NicknameRegex = new(
            "nickname=\"(?<name>[^\"]+)\"",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private bool _isRefreshingCombatLog;

        public ObservableCollection<CombatLogEntryViewModel> CombatLogEntries { get; } = new();

        private void InitializeCombatLogUi()
        {
            CombatLogStatusText.Text = "等待读取日志。";
            CombatLogSummaryText.Text = "将自动定位 Star Citizen 安装目录并解析最近击杀事件。";
            UpdateCombatLogRefreshButtonUi();
        }

        private void UpdateCombatLogRefreshButtonUi()
        {
            CombatLogRefreshButton.IsEnabled = !_isRefreshingCombatLog;
            CombatLogRefreshButton.Content = _isRefreshingCombatLog ? "读取中..." : "刷新日志";
        }

        private async void CombatLogRefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshCombatLogAsync();
        }

        private async Task RefreshCombatLogAsync()
        {
            if (_isRefreshingCombatLog)
            {
                return;
            }

            _isRefreshingCombatLog = true;
            UpdateCombatLogRefreshButtonUi();
            CombatLogStatusText.Text = "正在读取日志...";
            CombatLogSummaryText.Text = "正在定位安装目录并解析最近击杀事件。";
            CombatLogEntries.Clear();

            try
            {
                var snapshot = await Task.Run(LoadCombatLogSnapshot);

                foreach (var entry in snapshot.Entries.Take(MaxCombatLogEntries))
                {
                    CombatLogEntries.Add(new CombatLogEntryViewModel(entry));
                }

                if (!snapshot.LauncherLogFound)
                {
                    CombatLogStatusText.Text = "未找到 RSI Launcher 日志。";
                    CombatLogSummaryText.Text = "无法自动定位 Star Citizen 安装目录。";
                    return;
                }

                if (string.IsNullOrWhiteSpace(snapshot.InstallDirectory))
                {
                    CombatLogStatusText.Text = "尚未从 Launcher 日志定位到游戏目录。";
                    CombatLogSummaryText.Text = "请先通过 RSI Launcher 启动一次游戏，再回来刷新。";
                    return;
                }

                if (!snapshot.GameLogFound)
                {
                    CombatLogStatusText.Text = "已定位游戏目录，但没有找到 Game.log。";
                    CombatLogSummaryText.Text = $"当前目录：{snapshot.InstallDirectory}";
                    return;
                }

                if (string.IsNullOrWhiteSpace(snapshot.PlayerName))
                {
                    CombatLogStatusText.Text = "已定位 Game.log，但还没识别到当前玩家名。";
                    CombatLogSummaryText.Text = $"日志：{snapshot.GameLogPath}";
                    return;
                }

                if (snapshot.Entries.Count == 0)
                {
                    CombatLogStatusText.Text = "已读取日志，暂时没有最近击杀事件。";
                    CombatLogSummaryText.Text = $"当前玩家：{snapshot.PlayerName} | 目录：{snapshot.InstallDirectory}";
                    return;
                }

                CombatLogStatusText.Text = $"已读取最近 {CombatLogEntries.Count} 条击杀事件。";
                CombatLogSummaryText.Text = $"当前玩家：{snapshot.PlayerName} | 渠道：{snapshot.Channel} | 目录：{snapshot.InstallDirectory}";
            }
            catch (Exception ex)
            {
                CombatLogStatusText.Text = $"日志读取失败：{ex.Message}";
                CombatLogSummaryText.Text = "请确认 RSI Launcher 和 Star Citizen 日志文件可访问。";
            }
            finally
            {
                _isRefreshingCombatLog = false;
                UpdateCombatLogRefreshButtonUi();
            }
        }

        private static CombatLogSnapshot LoadCombatLogSnapshot()
        {
            var launcherLogPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "rsilauncher",
                "logs",
                "log.log");

            if (!File.Exists(launcherLogPath))
            {
                return CombatLogSnapshot.CreateMissingLauncherLog();
            }

            var launchContext = FindLatestLaunchContext(launcherLogPath);
            if (launchContext is null)
            {
                return CombatLogSnapshot.CreateMissingInstallDirectory();
            }

            var gameLogPath = Path.Combine(launchContext.InstallDirectory, "Game.log");
            if (!File.Exists(gameLogPath))
            {
                return CombatLogSnapshot.CreateMissingGameLog(launchContext.Channel, launchContext.InstallDirectory, gameLogPath);
            }

            var playerName = FindLocalPlayerName(gameLogPath);
            if (string.IsNullOrWhiteSpace(playerName))
            {
                return CombatLogSnapshot.CreateWithoutPlayerName(launchContext.Channel, launchContext.InstallDirectory, gameLogPath);
            }

            var entries = LoadCombatEntries(gameLogPath, launchContext.InstallDirectory, playerName);
            return CombatLogSnapshot.CreateSuccess(
                launchContext.Channel,
                launchContext.InstallDirectory,
                gameLogPath,
                playerName,
                entries);
        }

        private static LaunchContext? FindLatestLaunchContext(string launcherLogPath)
        {
            LaunchContext? latest = null;

            foreach (var line in ReadLinesShared(launcherLogPath))
            {
                var match = LauncherPathRegex.Match(line);
                if (!match.Success)
                {
                    continue;
                }

                latest = new LaunchContext(
                    match.Groups["channel"].Value.Trim(),
                    match.Groups["path"].Value.Trim());
            }

            return latest;
        }

        private static string? FindLocalPlayerName(string gameLogPath)
        {
            var lines = ReadLinesShared(gameLogPath).ToList();

            for (var index = lines.Count - 1; index >= 0; index--)
            {
                var line = lines[index];

                var nicknameMatch = NicknameRegex.Match(line);
                if (nicknameMatch.Success)
                {
                    return NormalizePlayerName(nicknameMatch.Groups["name"].Value);
                }

                var playerMatch = PlayerBracketRegex.Match(line);
                if (playerMatch.Success)
                {
                    return NormalizePlayerName(playerMatch.Groups["name"].Value);
                }
            }

            return null;
        }

        private static List<CombatLogEntry> LoadCombatEntries(string gameLogPath, string installDirectory, string playerName)
        {
            var collectedEntries = new List<CombatLogEntry>();
            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in EnumerateCombatLogFiles(gameLogPath, installDirectory))
            {
                foreach (var line in ReadLinesShared(path))
                {
                    if (!line.Contains("<Actor Death>", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var entry = ParseCombatLogEntry(line, playerName);
                    if (entry is null)
                    {
                        continue;
                    }

                    var dedupeKey = $"{entry.Timestamp:O}|{entry.Kind}|{entry.Killer}|{entry.Victim}|{entry.WeaponName}|{entry.DamageType}";
                    if (seenKeys.Add(dedupeKey))
                    {
                        collectedEntries.Add(entry);
                    }
                }
            }

            return collectedEntries
                .OrderByDescending(entry => entry.Timestamp)
                .Take(MaxCombatLogEntries)
                .ToList();
        }

        private static IEnumerable<string> EnumerateCombatLogFiles(string gameLogPath, string installDirectory)
        {
            yield return gameLogPath;

            var backupDirectory = Path.Combine(installDirectory, "logbackups");
            if (!Directory.Exists(backupDirectory))
            {
                yield break;
            }

            var backupPaths = new DirectoryInfo(backupDirectory)
                .EnumerateFiles("*.log", SearchOption.TopDirectoryOnly)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(MaxBackupLogsToScan)
                .Select(file => file.FullName);

            foreach (var backupPath in backupPaths)
            {
                yield return backupPath;
            }
        }

        private static CombatLogEntry? ParseCombatLogEntry(string line, string playerName)
        {
            var match = ActorDeathRegex.Match(line);
            if (!match.Success)
            {
                return null;
            }

            if (!DateTimeOffset.TryParse(match.Groups["timestamp"].Value, out var timestamp))
            {
                return null;
            }

            var victim = NormalizePlayerName(match.Groups["victim"].Value);
            var killer = NormalizePlayerName(match.Groups["killer"].Value);
            if (string.IsNullOrWhiteSpace(victim) || string.IsNullOrWhiteSpace(killer))
            {
                return null;
            }

            if (victim.Equals(playerName, StringComparison.OrdinalIgnoreCase) &&
                killer.Equals(playerName, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            CombatEventKind? kind = null;
            if (killer.Equals(playerName, StringComparison.OrdinalIgnoreCase))
            {
                kind = CombatEventKind.Kill;
            }
            else if (victim.Equals(playerName, StringComparison.OrdinalIgnoreCase))
            {
                kind = CombatEventKind.Death;
            }

            if (kind is null)
            {
                return null;
            }

            return new CombatLogEntry(
                timestamp,
                kind.Value,
                killer,
                victim,
                NormalizeDisplayValue(match.Groups["weapon"].Value, "未知武器"),
                NormalizeDisplayValue(match.Groups["damageType"].Value, "未知伤害"),
                NormalizeDisplayValue(match.Groups["zone"].Value, "未知区域"));
        }

        private static IEnumerable<string> ReadLinesShared(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream)
            {
                var line = reader.ReadLine();
                if (line is not null)
                {
                    yield return line;
                }
            }
        }

        private static string NormalizeDisplayValue(string? value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            {
                return fallback;
            }

            return value.Trim();
        }

        private static string? NormalizePlayerName(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var normalized = value.Trim();
            if (normalized.Length == 0)
            {
                return null;
            }

            return normalized;
        }

        public sealed class CombatLogEntryViewModel
        {
            public CombatLogEntryViewModel(CombatLogEntry entry)
            {
                BadgeText = entry.Kind == CombatEventKind.Kill ? "击杀" : "死亡";
                BadgeBackground = entry.Kind == CombatEventKind.Kill ? "#FF2D9D68" : "#FFB24B5A";
                Headline = entry.Kind == CombatEventKind.Kill
                    ? $"击杀 {entry.Victim}"
                    : $"被 {entry.Killer} 击杀";
                DetailLine = $"武器：{entry.WeaponName} | 伤害：{entry.DamageType}";
                MetaLine = $"时间：{entry.Timestamp.LocalDateTime:MM-dd HH:mm:ss} | 区域：{entry.ZoneName}";
            }

            public string BadgeText { get; }

            public string BadgeBackground { get; }

            public string Headline { get; }

            public string DetailLine { get; }

            public string MetaLine { get; }
        }

        private sealed class CombatLogSnapshot
        {
            public bool LauncherLogFound { get; init; }

            public bool GameLogFound { get; init; }

            public string Channel { get; init; } = "LIVE";

            public string? InstallDirectory { get; init; }

            public string? GameLogPath { get; init; }

            public string? PlayerName { get; init; }

            public List<CombatLogEntry> Entries { get; init; } = [];

            public static CombatLogSnapshot CreateMissingLauncherLog()
            {
                return new CombatLogSnapshot
                {
                    LauncherLogFound = false,
                    GameLogFound = false
                };
            }

            public static CombatLogSnapshot CreateMissingInstallDirectory()
            {
                return new CombatLogSnapshot
                {
                    LauncherLogFound = true,
                    GameLogFound = false
                };
            }

            public static CombatLogSnapshot CreateMissingGameLog(string channel, string installDirectory, string gameLogPath)
            {
                return new CombatLogSnapshot
                {
                    LauncherLogFound = true,
                    GameLogFound = false,
                    Channel = channel,
                    InstallDirectory = installDirectory,
                    GameLogPath = gameLogPath
                };
            }

            public static CombatLogSnapshot CreateWithoutPlayerName(string channel, string installDirectory, string gameLogPath)
            {
                return new CombatLogSnapshot
                {
                    LauncherLogFound = true,
                    GameLogFound = true,
                    Channel = channel,
                    InstallDirectory = installDirectory,
                    GameLogPath = gameLogPath
                };
            }

            public static CombatLogSnapshot CreateSuccess(
                string channel,
                string installDirectory,
                string gameLogPath,
                string playerName,
                List<CombatLogEntry> entries)
            {
                return new CombatLogSnapshot
                {
                    LauncherLogFound = true,
                    GameLogFound = true,
                    Channel = channel,
                    InstallDirectory = installDirectory,
                    GameLogPath = gameLogPath,
                    PlayerName = playerName,
                    Entries = entries
                };
            }
        }

        private sealed class LaunchContext
        {
            public LaunchContext(string channel, string installDirectory)
            {
                Channel = channel;
                InstallDirectory = installDirectory;
            }

            public string Channel { get; }

            public string InstallDirectory { get; }
        }

        public sealed class CombatLogEntry
        {
            public CombatLogEntry(
                DateTimeOffset timestamp,
                CombatEventKind kind,
                string killer,
                string victim,
                string weaponName,
                string damageType,
                string zoneName)
            {
                Timestamp = timestamp;
                Kind = kind;
                Killer = killer;
                Victim = victim;
                WeaponName = weaponName;
                DamageType = damageType;
                ZoneName = zoneName;
            }

            public DateTimeOffset Timestamp { get; }

            public CombatEventKind Kind { get; }

            public string Killer { get; }

            public string Victim { get; }

            public string WeaponName { get; }

            public string DamageType { get; }

            public string ZoneName { get; }
        }

        public enum CombatEventKind
        {
            Kill,
            Death
        }
    }
}
