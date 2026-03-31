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
        private static readonly Regex LauncherTimestampRegex = new(
            "\"t\":\"(?<timestamp>[^\"]+)\"",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex LauncherCrashRegex = new(
            @"Star Citizen process exited abnormally \(code: (?<code>\d+)\)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ActorDeathRegex = new(
            @"^<(?<timestamp>[^>]+)>.*?<Actor Death> CActor::Kill: '(?<victim>[^']+)' \[(?<victimId>\d+)\] in zone '(?<zone>[^']*)' killed by '(?<killer>[^']+)' \[(?<killerId>\d+)\] using '(?<weapon>[^']*)' \[Class (?<weaponClass>[^\]]+)\] with damage type '(?<damageType>[^']+)'",
            RegexOptions.Compiled);
        private static readonly Regex ChannelDisconnectedRegex = new(
            "^<(?<timestamp>[^>]+)>.*?<Channel Disconnected> cause=(?<cause>\\d+) reason=\"(?<reason>[^\"]+)\".*?nickname=\"(?<name>[^\"]+)\"",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex SystemQuitRegex = new(
            @"^<(?<timestamp>[^>]+)>.*?<SystemQuit> CSystem::Quit invoked with - cause=(?<cause>\d+), reason=(?<reason>[^,]+), exitCode=(?<exitCode>-?\d+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
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
            CombatLogSummaryText.Text = "将自动定位 Star Citizen 安装目录并解析最近战斗、上下线和异常事件。";
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
            CombatLogSummaryText.Text = "正在定位安装目录并解析最近战斗、上下线和异常事件。";
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
                    CombatLogStatusText.Text = "已读取日志，暂时没有最近事件。";
                    CombatLogSummaryText.Text = $"当前玩家：{snapshot.PlayerName} | 目录：{snapshot.InstallDirectory}";
                    return;
                }

                CombatLogStatusText.Text = $"已读取最近 {CombatLogEntries.Count} 条事件。";
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

            var entries = LoadCombatEntries(
                launcherLogPath,
                gameLogPath,
                launchContext.Channel,
                launchContext.InstallDirectory,
                playerName);

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

        private static List<CombatLogEntry> LoadCombatEntries(
            string launcherLogPath,
            string gameLogPath,
            string channel,
            string installDirectory,
            string playerName)
        {
            var collectedEntries = new List<CombatLogEntry>();
            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in LoadLauncherEntries(launcherLogPath, channel, installDirectory))
            {
                AddEventEntry(collectedEntries, seenKeys, entry);
            }

            foreach (var path in EnumerateCombatLogFiles(gameLogPath, installDirectory))
            {
                foreach (var line in ReadLinesShared(path))
                {
                    CombatLogEntry? entry = null;

                    if (line.Contains("<Actor Death>", StringComparison.Ordinal))
                    {
                        entry = ParseActorDeathEntry(line, playerName);
                    }
                    else if (line.Contains("<Channel Disconnected>", StringComparison.Ordinal))
                    {
                        entry = ParseDisconnectEntry(line, playerName);
                    }
                    else if (line.Contains("<SystemQuit>", StringComparison.Ordinal))
                    {
                        entry = ParseSystemQuitEntry(line);
                    }

                    AddEventEntry(collectedEntries, seenKeys, entry);
                }
            }

            return collectedEntries
                .OrderByDescending(entry => entry.Timestamp)
                .Take(MaxCombatLogEntries)
                .ToList();
        }

        private static void AddEventEntry(
            ICollection<CombatLogEntry> collectedEntries,
            ISet<string> seenKeys,
            CombatLogEntry? entry)
        {
            if (entry is null)
            {
                return;
            }

            if (seenKeys.Add(entry.DeduplicationKey))
            {
                collectedEntries.Add(entry);
            }
        }

        private static IEnumerable<CombatLogEntry> LoadLauncherEntries(
            string launcherLogPath,
            string currentChannel,
            string installDirectory)
        {
            foreach (var line in ReadLinesShared(launcherLogPath))
            {
                if (!line.Contains("[Launcher::launch]", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!TryParseLauncherTimestamp(line, out var timestamp))
                {
                    continue;
                }

                var launchMatch = LauncherPathRegex.Match(line);
                if (launchMatch.Success)
                {
                    var path = launchMatch.Groups["path"].Value.Trim();
                    if (!path.Equals(installDirectory, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var channel = launchMatch.Groups["channel"].Value.Trim();
                    yield return new CombatLogEntry(
                        timestamp,
                        CombatEventKind.Login,
                        "启动游戏",
                        $"渠道：{channel}",
                        $"目录：{path}",
                        $"launcher-login|{timestamp:O}|{path}");
                    continue;
                }

                if (!line.Contains(installDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var crashMatch = LauncherCrashRegex.Match(line);
                if (!crashMatch.Success)
                {
                    continue;
                }

                var exitCode = crashMatch.Groups["code"].Value.Trim();
                yield return new CombatLogEntry(
                    timestamp,
                    CombatEventKind.AbnormalExit,
                    "游戏异常退出",
                    $"退出码：{exitCode}",
                    $"渠道：{currentChannel} | 来源：Launcher",
                    $"launcher-crash|{timestamp:O}|{exitCode}");
            }
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

        private static CombatLogEntry? ParseActorDeathEntry(string line, string playerName)
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

            var weaponName = NormalizeDisplayValue(match.Groups["weapon"].Value, "未知武器");
            var damageType = NormalizeDisplayValue(match.Groups["damageType"].Value, "未知伤害");
            var zoneName = NormalizeDisplayValue(match.Groups["zone"].Value, "未知区域");

            var victimIsLocal = victim.Equals(playerName, StringComparison.OrdinalIgnoreCase);
            var killerIsLocal = killer.Equals(playerName, StringComparison.OrdinalIgnoreCase);
            if (!victimIsLocal && !killerIsLocal)
            {
                return null;
            }

            if (victimIsLocal && killerIsLocal && damageType.Equals("Suicide", StringComparison.OrdinalIgnoreCase))
            {
                return new CombatLogEntry(
                    timestamp,
                    CombatEventKind.Suicide,
                    "自杀",
                    $"伤害：{damageType} | 武器：{weaponName}",
                    $"区域：{zoneName}",
                    $"actor-suicide|{timestamp:O}|{zoneName}");
            }

            if (victimIsLocal && IsCrashDamageType(damageType))
            {
                return new CombatLogEntry(
                    timestamp,
                    CombatEventKind.Crash,
                    "坠毁死亡",
                    $"伤害：{damageType} | 来源：{killer}",
                    $"区域：{zoneName}",
                    $"actor-crash|{timestamp:O}|{killer}|{damageType}|{zoneName}");
            }

            if (killerIsLocal)
            {
                return new CombatLogEntry(
                    timestamp,
                    CombatEventKind.Kill,
                    $"击杀 {victim}",
                    $"武器：{weaponName} | 伤害：{damageType}",
                    $"区域：{zoneName}",
                    $"actor-kill|{timestamp:O}|{victim}|{weaponName}|{damageType}");
            }

            return new CombatLogEntry(
                timestamp,
                CombatEventKind.Death,
                $"被 {killer} 击杀",
                $"武器：{weaponName} | 伤害：{damageType}",
                $"区域：{zoneName}",
                $"actor-death|{timestamp:O}|{killer}|{weaponName}|{damageType}");
        }

        private static CombatLogEntry? ParseDisconnectEntry(string line, string playerName)
        {
            var match = ChannelDisconnectedRegex.Match(line);
            if (!match.Success)
            {
                return null;
            }

            var name = NormalizePlayerName(match.Groups["name"].Value);
            if (!string.Equals(name, playerName, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (!DateTimeOffset.TryParse(match.Groups["timestamp"].Value, out var timestamp))
            {
                return null;
            }

            var reason = match.Groups["reason"].Value.Trim();
            if (reason.Equals("Nub destroyed", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var cause = match.Groups["cause"].Value.Trim();
            var normalizedReason = NormalizeDisconnectReason(reason);
            var kind = IsLogoutReason(reason) ? CombatEventKind.Logout : CombatEventKind.Disconnect;
            var headline = kind == CombatEventKind.Logout ? "退出当前会话" : "连接断开";

            return new CombatLogEntry(
                timestamp,
                kind,
                headline,
                $"原因：{normalizedReason}",
                $"代码：{cause} | 来源：Game.log",
                $"disconnect|{timestamp:O}|{kind}|{cause}|{normalizedReason}");
        }

        private static CombatLogEntry? ParseSystemQuitEntry(string line)
        {
            var match = SystemQuitRegex.Match(line);
            if (!match.Success)
            {
                return null;
            }

            if (!DateTimeOffset.TryParse(match.Groups["timestamp"].Value, out var timestamp))
            {
                return null;
            }

            var reason = match.Groups["reason"].Value.Trim();
            var cause = match.Groups["cause"].Value.Trim();
            var exitCode = match.Groups["exitCode"].Value.Trim();
            var isNormalExit = exitCode == "0";
            var kind = isNormalExit ? CombatEventKind.Logout : CombatEventKind.AbnormalExit;
            var headline = isNormalExit ? "退出游戏" : "游戏异常退出";

            return new CombatLogEntry(
                timestamp,
                kind,
                headline,
                $"原因：{NormalizeSystemQuitReason(reason)} | 退出码：{exitCode}",
                $"代码：{cause} | 来源：Game.log",
                $"system-quit|{timestamp:O}|{kind}|{cause}|{exitCode}|{reason}");
        }

        private static bool TryParseLauncherTimestamp(string line, out DateTimeOffset timestamp)
        {
            timestamp = default;

            var match = LauncherTimestampRegex.Match(line);
            if (!match.Success)
            {
                return false;
            }

            var rawValue = match.Groups["timestamp"].Value.Trim();
            if (DateTimeOffset.TryParse(rawValue, out timestamp))
            {
                return true;
            }

            if (!DateTime.TryParse(rawValue, out var localDateTime))
            {
                return false;
            }

            timestamp = new DateTimeOffset(DateTime.SpecifyKind(localDateTime, DateTimeKind.Local));
            return true;
        }

        private static bool IsCrashDamageType(string damageType)
        {
            return damageType.Equals("Crash", StringComparison.OrdinalIgnoreCase) ||
                   damageType.Equals("Collision", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsLogoutReason(string reason)
        {
            return reason.Contains("Player requested disconnect", StringComparison.OrdinalIgnoreCase) ||
                   reason.Contains("ExitToMenu", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeDisconnectReason(string reason)
        {
            if (reason.Contains("Player requested disconnect", StringComparison.OrdinalIgnoreCase))
            {
                return "玩家主动请求断开连接";
            }

            if (reason.Contains("ExitToMenu", StringComparison.OrdinalIgnoreCase))
            {
                return "返回主菜单";
            }

            if (reason.Contains("DGS disconnecting all channels before game shutdown", StringComparison.OrdinalIgnoreCase))
            {
                return "服务器关闭前断开全部连接";
            }

            return reason;
        }

        private static string NormalizeSystemQuitReason(string reason)
        {
            if (reason.Equals("User closed the application", StringComparison.OrdinalIgnoreCase))
            {
                return "用户关闭了游戏";
            }

            if (reason.Equals("Quit via console command", StringComparison.OrdinalIgnoreCase))
            {
                return "通过命令触发退出";
            }

            return reason;
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
                var style = GetBadgeStyle(entry.Kind);
                BadgeText = style.Text;
                BadgeBackground = style.Background;
                Headline = entry.Headline;
                DetailLine = entry.DetailLine;
                MetaLine = $"时间：{entry.Timestamp.LocalDateTime:MM-dd HH:mm:ss} | {entry.ContextLine}";
            }

            public string BadgeText { get; }

            public string BadgeBackground { get; }

            public string Headline { get; }

            public string DetailLine { get; }

            public string MetaLine { get; }

            private static (string Text, string Background) GetBadgeStyle(CombatEventKind kind)
            {
                return kind switch
                {
                    CombatEventKind.Kill => ("击杀", "#FF2D9D68"),
                    CombatEventKind.Death => ("死亡", "#FFB24B5A"),
                    CombatEventKind.Suicide => ("自杀", "#FFC9892F"),
                    CombatEventKind.Crash => ("坠毁", "#FFD66B3B"),
                    CombatEventKind.Login => ("上线", "#FF1776A8"),
                    CombatEventKind.Logout => ("下线", "#FF6B7C8F"),
                    CombatEventKind.Disconnect => ("断线", "#FFC44F7A"),
                    CombatEventKind.AbnormalExit => ("崩溃", "#FFB13C3C"),
                    _ => ("事件", "#FF1776A8")
                };
            }
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
                string headline,
                string detailLine,
                string contextLine,
                string deduplicationKey)
            {
                Timestamp = timestamp;
                Kind = kind;
                Headline = headline;
                DetailLine = detailLine;
                ContextLine = contextLine;
                DeduplicationKey = deduplicationKey;
            }

            public DateTimeOffset Timestamp { get; }

            public CombatEventKind Kind { get; }

            public string Headline { get; }

            public string DetailLine { get; }

            public string ContextLine { get; }

            public string DeduplicationKey { get; }
        }

        public enum CombatEventKind
        {
            Kill,
            Death,
            Suicide,
            Crash,
            Login,
            Logout,
            Disconnect,
            AbnormalExit
        }
    }
}
