using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace StarCitizenOverLay
{
    internal sealed class GlobalKeyboardShortcutListener : IDisposable
    {
        private const int PollIntervalMs = 25;

        private static readonly HotKeyChord InteractionChord = new(0x61, 0x23);
        private static readonly HotKeyChord VisibilityChord = new(0x60, 0x2D);

        private volatile bool _isRunning;
        private Thread? _thread;

        public event EventHandler? InteractionHotKeyPressed;
        public event EventHandler? VisibilityHotKeyPressed;

        public void Start()
        {
            if (_isRunning)
            {
                return;
            }

            _isRunning = true;
            _thread = new Thread(PollLoop)
            {
                IsBackground = true,
                Name = "OverlayHotkeyPoller"
            };
            _thread.Start();
        }

        public void Dispose()
        {
            if (!_isRunning)
            {
                return;
            }

            _isRunning = false;
            _thread?.Join(500);
            _thread = null;
        }

        private void PollLoop()
        {
            var previousInteractionPressed = false;
            var previousVisibilityPressed = false;

            while (_isRunning)
            {
                var interactionPressed = IsChordPressed(InteractionChord);
                var visibilityPressed = IsChordPressed(VisibilityChord);

                if (interactionPressed && !previousInteractionPressed)
                {
                    InteractionHotKeyPressed?.Invoke(this, EventArgs.Empty);
                }

                if (visibilityPressed && !previousVisibilityPressed)
                {
                    VisibilityHotKeyPressed?.Invoke(this, EventArgs.Empty);
                }

                previousInteractionPressed = interactionPressed;
                previousVisibilityPressed = visibilityPressed;
                Thread.Sleep(PollIntervalMs);
            }
        }

        private static bool IsChordPressed(HotKeyChord chord)
        {
            return chord.CandidateVirtualKeys.Any(IsKeyDown);
        }

        private static bool IsKeyDown(int virtualKey)
        {
            return (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
        }

        private readonly record struct HotKeyChord(params int[] CandidateVirtualKeys);
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);
    }
}
