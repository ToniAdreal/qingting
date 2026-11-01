using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SpaceDictation {
    internal sealed class SpaceKey : IDisposable {
        delegate IntPtr KeyboardProc(int code, IntPtr msg, IntPtr data);
        [StructLayout(LayoutKind.Sequential)] struct KeyData { public uint key, scan, flags, time; public UIntPtr extra; }
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, KeyboardProc callback, IntPtr module, uint thread);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr msg, IntPtr data);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
        const int Space = 0x20;
        readonly KeyboardProc callback;
        readonly Func<bool> enabled;
        readonly Action start, stop;
        readonly Action otherKey;
        readonly Action<byte, byte, uint, UIntPtr> emit;
        readonly Timer threshold = new Timer { Interval = 340 };
        IntPtr hook, origin;
        enum Press { Up, Native, Pending, Held, Consumed }
        Press press;

        public SpaceKey(Func<bool> enabled, Action start, Action stop, Action<byte, byte, uint, UIntPtr> emit = null, Action otherKey = null) {
            this.enabled = enabled; this.start = start; this.stop = stop;
            this.otherKey = otherKey;
            this.emit = emit ?? keybd_event;
            threshold.Tick += delegate {
                threshold.Stop();
                if (press != Press.Pending) return;
                if (GetForegroundWindow() != origin || !enabled()) { press = Press.Consumed; return; }
                press = Press.Held;
                start();
            };
            callback = Handle;
            hook = SetWindowsHookEx(13, callback, GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        static bool Modified() {
            return GetAsyncKeyState(0x10) < 0 || GetAsyncKeyState(0x11) < 0 || GetAsyncKeyState(0x12) < 0 ||
                GetAsyncKeyState(0x5b) < 0 || GetAsyncKeyState(0x5c) < 0;
        }
        void SpaceTap() {
            emit(Space, 0x39, 0, UIntPtr.Zero);
            emit(Space, 0x39, 2, UIntPtr.Zero);
        }
        IntPtr Handle(int code, IntPtr msg, IntPtr pointer) {
            if (code >= 0) {
                var data = (KeyData)Marshal.PtrToStructure(pointer, typeof(KeyData));
                bool down = (data.flags & 0x80) == 0;
                if ((data.flags & 0x10) == 0) {
                    if (down && data.key != Space && otherKey != null) otherKey();
                    if (data.key == Space) {
                        if (down && press == Press.Up) {
                            press = enabled() && !Modified() ? Press.Pending : Press.Native;
                            if (press == Press.Pending) { origin = GetForegroundWindow(); threshold.Start(); }
                        }
                        var previous = press;
                        if (!down) {
                            threshold.Stop(); press = Press.Up;
                            if (previous == Press.Pending && GetForegroundWindow() == origin) SpaceTap();
                            if (previous == Press.Held) stop();
                        }
                        if (previous != Press.Up && previous != Press.Native) return (IntPtr)1;
                    } else if (down && press == Press.Pending) {
                        // Fast typing often presses the next letter before releasing space.
                        // Replay both in order so a delayed space never lands after the letter.
                        threshold.Stop(); press = Press.Consumed;
                        if (GetForegroundWindow() == origin) {
                            SpaceTap();
                            emit((byte)data.key, (byte)data.scan, data.flags & 1, UIntPtr.Zero);
                            return (IntPtr)1;
                        }
                    }
                }
            }
            return CallNextHookEx(hook, code, msg, pointer);
        }
        public void Dispose() {
            threshold.Dispose();
            if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
        }
    }
}
