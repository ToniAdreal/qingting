using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace SpaceDictation {
    // A web page may put several editors behind the same HWND. Match the focused
    // accessibility element too. We never read the field's text or move focus.
    internal sealed class PasteTarget {
        [StructLayout(LayoutKind.Sequential)]
        struct GuiThreadInfo {
            public int size, flags;
            public IntPtr active, focus, capture, menu, move, caret;
            public int left, top, right, bottom;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct KeyboardInput {
            public ushort key, scan;
            public uint flags, time;
            public UIntPtr extra;
        }
        [StructLayout(LayoutKind.Explicit, Size = 32)]
        struct InputData { [FieldOffset(0)] public KeyboardInput keyboard; } // x64 INPUT union
        [StructLayout(LayoutKind.Sequential)]
        struct Input { public uint type; public InputData data; }
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr pid);
        [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] static extern uint SendInput(uint count, Input[] items, int size);
        [DllImport("user32.dll")] internal static extern uint GetClipboardSequenceNumber();

        readonly IntPtr window, focus;
        readonly int[] elementId;
        PasteTarget(IntPtr window, IntPtr focus, int[] elementId) {
            this.window = window; this.focus = focus; this.elementId = elementId;
        }
        static IntPtr FocusOf(IntPtr window) {
            if (window == IntPtr.Zero || GetForegroundWindow() != window) return IntPtr.Zero;
            var info = new GuiThreadInfo { size = Marshal.SizeOf(typeof(GuiThreadInfo)) };
            uint thread = GetWindowThreadProcessId(window, IntPtr.Zero);
            return thread != 0 && GetGUIThreadInfo(thread, ref info) ? info.focus : IntPtr.Zero;
        }
        static int[] FocusedEditor() {
            var element = AutomationElement.FocusedElement;
            if (element == null) return null;
            var state = element.Current;
            if (state.IsPassword || !state.IsEnabled || !state.HasKeyboardFocus) return null;
            object pattern;
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out pattern)) {
                if (((ValuePattern)pattern).Current.IsReadOnly) return null;
            } else {
                if ((state.ControlType != ControlType.Edit && state.ControlType != ControlType.Document) ||
                    !element.TryGetCurrentPattern(TextPattern.Pattern, out pattern)) return null;
                var value = ((TextPattern)pattern).DocumentRange.GetAttributeValue(TextPattern.IsReadOnlyAttribute);
                if (!Object.Equals(value, false)) return null;
            }
            return element.GetRuntimeId();
        }
        public static Task<PasteTarget> Capture() {
            IntPtr window = GetForegroundWindow(), focus = FocusOf(window);
            return Task.Run(delegate {
                try {
                    if (focus == IntPtr.Zero || FocusOf(window) != focus) return null;
                    var id = FocusedEditor();
                    return id != null && id.Length > 0 && FocusOf(window) == focus ? new PasteTarget(window, focus, id) : null;
                } catch (Exception) { return null; } // Some providers do not expose a focused editor.
            });
        }
        static Input Key(ushort key, bool up) {
            return new Input { type = 1, data = new InputData { keyboard = new KeyboardInput { key = key, flags = up ? 2u : 0u } } };
        }
        public static async Task<bool> PasteIfCurrent(Task<PasteTarget> capture, uint sequence) {
            if (capture == null || sequence == 0 || !capture.IsCompleted) return false;
            var target = await capture;
            if (target == null) return false;
            // Only the metadata lookup runs off the UI thread. A timed-out lookup can
            // never inject a late keystroke.
            var lookup = Task.Run(delegate {
                try { return FocusOf(target.window) == target.focus && target.elementId.SequenceEqual(FocusedEditor() ?? new int[0]); }
                catch (Exception) { return false; }
            });
            if (await Task.WhenAny(lookup, Task.Delay(800)) != lookup || !await lookup) return false;
            if (FocusOf(target.window) != target.focus || GetClipboardSequenceNumber() != sequence) return false;
            foreach (int key in new[] { 0x10, 0x11, 0x12, 0x5b, 0x5c, 0x56 })
                if (GetAsyncKeyState(key) < 0) return false;
            var chord = new[] { Key(0x11, false), Key(0x56, false), Key(0x56, true), Key(0x11, true) };
            return SendInput((uint)chord.Length, chord, Marshal.SizeOf(typeof(Input))) == chord.Length;
        }
    }
}
