using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StarCitizenOverLay
{
    internal sealed class OverlayShellState
    {
        private Func<Task>? _combatLogRefreshHandler;
        private Func<Task>? _exitHandler;
        private List<OverlayShellLogEntry> _combatLogEntries = [];

        public event Action? Changed;

        public bool IsInteractive { get; private set; } = true;

        public string InteractionModeTitle { get; private set; } = "可交互";

        public string InteractionDescription { get; private set; } =
            "当前是全屏透明宿主层。可交互模式下可以点击面板内容。";

        public string InteractionToggleHint => "交互切换：小键盘 1";

        public string VisibilityToggleHint => "显示隐藏：小键盘 0";

        public string ExitHint => "退出程序：复选框面板或托盘菜单";

        public bool IsCombatLogRefreshing { get; private set; }

        public string CombatLogStatus { get; private set; } = "等待读取日志。";

        public string CombatLogSummary { get; private set; } =
            "将自动定位 Star Citizen 安装目录并解析最近战斗、上下线和异常事件。";

        public IReadOnlyList<OverlayShellLogEntry> CombatLogEntries => _combatLogEntries;

        public void SetCombatLogRefreshHandler(Func<Task>? handler)
        {
            _combatLogRefreshHandler = handler;
        }

        public Task RequestCombatLogRefreshAsync()
        {
            return _combatLogRefreshHandler?.Invoke() ?? Task.CompletedTask;
        }

        public void SetExitHandler(Func<Task>? handler)
        {
            _exitHandler = handler;
        }

        public Task RequestExitAsync()
        {
            return _exitHandler?.Invoke() ?? Task.CompletedTask;
        }

        public void SetInteractionState(bool isInteractive, string title, string description)
        {
            IsInteractive = isInteractive;
            InteractionModeTitle = string.IsNullOrWhiteSpace(title) ? "可交互" : title;
            InteractionDescription = string.IsNullOrWhiteSpace(description)
                ? "当前是全屏透明宿主层。可交互模式下可以点击面板内容。"
                : description;

            NotifyChanged();
        }

        public void SetCombatLogState(
            bool isRefreshing,
            string status,
            string summary,
            IEnumerable<OverlayShellLogEntry>? entries)
        {
            IsCombatLogRefreshing = isRefreshing;
            CombatLogStatus = string.IsNullOrWhiteSpace(status) ? "等待读取日志。" : status;
            CombatLogSummary = string.IsNullOrWhiteSpace(summary)
                ? "将自动定位 Star Citizen 安装目录并解析最近战斗、上下线和异常事件。"
                : summary;
            _combatLogEntries = entries?.ToList() ?? [];

            NotifyChanged();
        }

        private void NotifyChanged()
        {
            Changed?.Invoke();
        }
    }

    internal sealed record OverlayShellLogEntry(
        string BadgeText,
        string BadgeBackground,
        string TimeText,
        string Headline,
        string DetailLine,
        string ContextLine);
}
