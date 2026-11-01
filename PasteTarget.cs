using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace SpaceDictation {
    internal sealed class PasteGuard : IDisposable {
        delegate IntPtr MouseProc(int code, IntPtr message, IntPtr data);
        [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, MouseProc callback, IntPtr module, uint thread);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        readonly MouseProc callback;
        IntPtr hook;
        public bool Untouched { get; private set; }
        public PasteGuard() {
            callback = delegate(int code, IntPtr message, IntPtr data) {
                if (code >= 0 && message.ToInt32() != 0x200) Cancel(); // Mouse movement alone is harmless.
                return CallNextHookEx(hook, code, message, data);
            };
            hook = SetWindowsHookEx(14, callback, GetModuleHandle(null), 0);
            Untouched = hook != IntPtr.Zero;
        }
        public void Cancel() { Untouched = false; }
        public void Dispose() {
            Cancel();
            if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
        }
    }

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
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr pid);
        [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] static extern uint SendInput(uint count, Input[] items, int size);
        [DllImport("user32.dll")] static extern uint MapVirtualKey(uint key, uint mode);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder name, int length);
        [DllImport("user32.dll")] internal static extern uint GetClipboardSequenceNumber();

        readonly IntPtr window, focus;
        readonly int[] elementId;
        readonly Task<int[]> chromeEditor;
        internal static string LastReason { get; private set; }
        PasteTarget(IntPtr window, IntPtr focus, int[] elementId, Task<int[]> chromeEditor = null) {
            this.window = window; this.focus = focus; this.elementId = elementId; this.chromeEditor = chromeEditor;
        }
        public bool RestoreWindow() { return SetForegroundWindow(window) || GetForegroundWindow() == window; }
        static IntPtr FocusOf(IntPtr window) {
            if (window == IntPtr.Zero || GetForegroundWindow() != window) return IntPtr.Zero;
            var info = new GuiThreadInfo { size = Marshal.SizeOf(typeof(GuiThreadInfo)) };
            uint thread = GetWindowThreadProcessId(window, IntPtr.Zero);
            return thread != 0 && GetGUIThreadInfo(thread, ref info) ? info.focus : IntPtr.Zero;
        }
        static bool ChromiumWindow(IntPtr window) {
            var name = new StringBuilder(64);
            GetClassName(window, name, name.Capacity);
            return name.ToString().StartsWith("Chrome_WidgetWin_", StringComparison.Ordinal);
        }
        static int[] FocusedEditor(bool chromium) {
            var element = AutomationElement.FocusedElement;
            if (element == null) return null;
            var state = element.Current;
            if (state.IsPassword || !state.IsEnabled) return null;
            // Electron can expose the renderer document instead of its contenteditable.
            // A document identity alone is insufficient. The input guard also rejects
            // any intervening click, scroll or non-space key before paste.
            if (chromium &&
                (state.ControlType == ControlType.Document || state.ControlType == ControlType.Pane))
                return element.GetRuntimeId();
            if (!state.HasKeyboardFocus) return null;
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
        // Chromium may publish a document first and the real editor later after a
        // cold start. A missing editor is distinct from an explicitly protected field.
        static int[] ChromeEditor() {
            try {
                var element = AutomationElement.FocusedElement;
                if (element == null) return null;
                var state = element.Current;
                if (state.IsPassword || !state.IsEnabled) return new int[0];
                if (state.ControlType == ControlType.Document || state.ControlType == ControlType.Pane ||
                    state.ControlType == ControlType.Window || state.ControlType == ControlType.Custom) return null;
                if (state.ControlType != ControlType.Edit) return new int[0];
                object pattern;
                if (element.TryGetCurrentPattern(ValuePattern.Pattern, out pattern) &&
                    ((ValuePattern)pattern).Current.IsReadOnly) return new int[0];
                return element.GetRuntimeId();
            } catch (Exception) { return null; }
        }
        public static Task<PasteTarget> Capture(bool guardedChrome = false) {
            IntPtr window = GetForegroundWindow(), focus = FocusOf(window);
            if (guardedChrome && ChromiumWindow(window) && window != IntPtr.Zero)
                return Task.FromResult(new PasteTarget(window, focus, null, Task.Run((Func<int[]>)ChromeEditor)));
            return Task.Run(delegate {
                try {
                    if (focus == IntPtr.Zero || FocusOf(window) != focus) return null;
                    var id = FocusedEditor(ChromiumWindow(window));
                    return id != null && id.Length > 0 && FocusOf(window) == focus ? new PasteTarget(window, focus, id) : null;
                } catch (Exception) { return null; } // Some providers do not expose a focused editor.
            });
        }
        static Input Key(ushort key, bool up) {
            return new Input { type = 1, data = new InputData { keyboard = new KeyboardInput { scan = (ushort)MapVirtualKey(key, 0), flags = up ? 10u : 8u } } };
        }
        static bool Skip(string why) { LastReason = why; return false; }
        public static async Task<bool> PasteIfCurrent(Task<PasteTarget> capture, uint sequence, Func<bool> inputUnchanged = null, string expectedText = null) {
            if (capture == null || sequence == 0) return Skip("未找到原输入框");
            if (await Task.WhenAny(capture, Task.Delay(2000)) != capture) return Skip("输入框响应超时");
            var target = await capture;
            if (target == null) return Skip("未找到可编辑的输入框");
            if (GetForegroundWindow() != target.window) return Skip("已切换窗口");
            if (target.chromeEditor != null) {
                if (inputUnchanged == null || !inputUnchanged()) return Skip("输入位置已变化");
                var current = Task.Run((Func<int[]>)ChromeEditor);
                await Task.WhenAny(Task.WhenAll(target.chromeEditor, current), Task.Delay(1200));
                var before = target.chromeEditor.IsCompleted ? target.chromeEditor.Result : null;
                var after = current.IsCompleted ? current.Result : null;
                if ((before != null && before.Length == 0) || (after != null && after.Length == 0))
                    return Skip("当前控件不支持粘贴");
                if (before != null && after != null && !before.SequenceEqual(after)) return Skip("已切换输入框");
            } else {
                var lookup = Task.Run(delegate {
                    try { return target.elementId.SequenceEqual(FocusedEditor(ChromiumWindow(target.window)) ?? new int[0]); }
                    catch (Exception) { return false; }
                });
                if (await Task.WhenAny(lookup, Task.Delay(2000)) != lookup || !await lookup) return Skip("输入框已变化或未响应");
            }
            if (inputUnchanged != null && !inputUnchanged()) return Skip("检测到其他按键或点击");
            if (GetClipboardSequenceNumber() != sequence) {
                // Clipboard managers can republish the same Unicode text during the focus check.
                try { if (expectedText == null || System.Windows.Forms.Clipboard.GetText() != expectedText) return Skip("剪贴板已变化"); }
                catch (System.Runtime.InteropServices.ExternalException) { return Skip("剪贴板被占用"); }
            }
            if (GetForegroundWindow() != target.window || FocusOf(target.window) != target.focus) return Skip("已切换输入框");
            foreach (int key in new[] { 0x10, 0x11, 0x12, 0x5b, 0x5c, 0x56 })
                if (GetAsyncKeyState(key) < 0) return Skip("有组合键按住");
            var name = new StringBuilder(128);
            GetClassName(target.focus, name, name.Capacity);
            string control = name.ToString().ToUpperInvariant();
            if (control == "EDIT" || control.StartsWith("RICHEDIT") || control.StartsWith("WINDOWSFORMS10.EDIT."))
                return PostMessage(target.focus, 0x0302, IntPtr.Zero, IntPtr.Zero) || Skip("输入框拒绝粘贴");
            var chord = new[] { Key(0x11, false), Key(0x56, false), Key(0x56, true), Key(0x11, true) };
            return SendInput((uint)chord.Length, chord, Marshal.SizeOf(typeof(Input))) == chord.Length || Skip("窗口拒绝粘贴快捷键");
        }
    }
}
