using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SpaceDictation {
    // A small ring of pinned native buffers. Capture is active only between Start and Stop.
    internal sealed class Recorder : IDisposable {
        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        struct Format {
            public ushort tag, channels;
            public uint rate, bytesPerSecond;
            public ushort align, bits, extra;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct Header {
            public IntPtr data;
            public uint length, recorded;
            public UIntPtr user;
            public uint flags, loops;
            public IntPtr next;
            public UIntPtr reserved;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct Caps {
            public ushort manufacturer, product;
            public uint version;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string name;
            public uint formats;
            public ushort channels, pad;
        }
        [DllImport("winmm.dll")] static extern uint waveInGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern uint waveInGetDevCapsW(UIntPtr id, out Caps caps, uint size);
        [DllImport("winmm.dll")] static extern uint waveInOpen(out IntPtr input, uint device, ref Format format, IntPtr callback, IntPtr instance, uint flags);
        [DllImport("winmm.dll")] static extern uint waveInPrepareHeader(IntPtr input, IntPtr header, uint size);
        [DllImport("winmm.dll")] static extern uint waveInUnprepareHeader(IntPtr input, IntPtr header, uint size);
        [DllImport("winmm.dll")] static extern uint waveInAddBuffer(IntPtr input, IntPtr header, uint size);
        [DllImport("winmm.dll")] static extern uint waveInStart(IntPtr input);
        [DllImport("winmm.dll")] static extern uint waveInReset(IntPtr input);
        [DllImport("winmm.dll")] static extern uint waveInClose(IntPtr input);

        readonly IntPtr[] headers = new IntPtr[16], buffers = new IntPtr[16];
        readonly bool[] prepared = new bool[16];
        const int Chunk = 3200;
        static readonly uint HeaderSize = (uint)Marshal.SizeOf(typeof(Header));
        readonly byte[] samples = new byte[Chunk];
        readonly MemoryStream pcm = new MemoryStream();
        IntPtr input;
        int next;
        public int Peak { get; private set; }
        public int Bytes { get { return (int)pcm.Length; } }

        public static Dictionary<uint, string> Devices() {
            var devices = new Dictionary<uint, string>();
            for (uint i = 0; i < waveInGetNumDevs(); i++) {
                Caps caps;
                if (waveInGetDevCapsW((UIntPtr)i, out caps, (uint)Marshal.SizeOf(typeof(Caps))) == 0)
                    devices.Add(i, caps.name);
            }
            return devices;
        }
        static void Check(uint error) {
            if (error != 0) throw new IOException("麦克风无法录音（设备代码 " + error + "）。请检查设备连接、Windows 麦克风权限，或在设置中切换输入。");
        }
        public Recorder(string deviceName) {
            uint device = uint.MaxValue;
            if (!String.IsNullOrEmpty(deviceName)) {
                foreach (var item in Devices()) if (item.Value == deviceName) { device = item.Key; break; }
                if (device == uint.MaxValue) throw new IOException("已选麦克风没有连接。请连接设备或在设置中重新选择。");
            }
            var format = new Format { tag = 1, channels = 1, rate = 16000, bytesPerSecond = 32000, align = 2, bits = 16 };
            try {
                Check(waveInOpen(out input, device, ref format, IntPtr.Zero, IntPtr.Zero, 0));
                for (int i = 0; i < headers.Length; i++) {
                    headers[i] = Marshal.AllocHGlobal((int)HeaderSize);
                    buffers[i] = Marshal.AllocHGlobal(Chunk);
                    Marshal.StructureToPtr(new Header { data = buffers[i], length = Chunk }, headers[i], false);
                    Check(waveInPrepareHeader(input, headers[i], HeaderSize));
                    prepared[i] = true;
                    Check(waveInAddBuffer(input, headers[i], HeaderSize));
                }
                Check(waveInStart(input));
            } catch { Dispose(); throw; }
        }
        public void Drain(bool refill) {
            Peak = 0;
            for (int count = 0; count < headers.Length; count++) {
                var head = (Header)Marshal.PtrToStructure(headers[next], typeof(Header));
                if ((head.flags & 1) == 0) break;
                int length = (int)head.recorded;
                Marshal.Copy(head.data, samples, 0, length);
                pcm.Write(samples, 0, length);
                for (int n = 0; n + 1 < length; n += 2)
                    Peak = Math.Max(Peak, Math.Abs((int)BitConverter.ToInt16(samples, n)));
                if (refill) Check(waveInAddBuffer(input, headers[next], HeaderSize));
                next = (next + 1) % headers.Length;
            }
        }
        public byte[] Finish() {
            Check(waveInReset(input));
            Drain(false);
            return MakeWave(pcm.ToArray());
        }
        internal static byte[] MakeWave(byte[] bytes) {
            using (var stream = new MemoryStream(44 + bytes.Length))
            using (var w = new BinaryWriter(stream)) {
                w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(bytes.Length + 36);
                w.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16);
                w.Write((short)1); w.Write((short)1); w.Write(16000); w.Write(32000);
                w.Write((short)2); w.Write((short)16);
                w.Write(Encoding.ASCII.GetBytes("data")); w.Write(bytes.Length); w.Write(bytes);
                return stream.ToArray();
            }
        }
        public void Dispose() {
            if (input != IntPtr.Zero) waveInReset(input);
            for (int i = 0; i < headers.Length; i++) {
                if (prepared[i]) { waveInUnprepareHeader(input, headers[i], HeaderSize); prepared[i] = false; }
                if (headers[i] != IntPtr.Zero) Marshal.FreeHGlobal(headers[i]);
                if (buffers[i] != IntPtr.Zero) Marshal.FreeHGlobal(buffers[i]);
                headers[i] = buffers[i] = IntPtr.Zero;
            }
            if (input != IntPtr.Zero) { waveInClose(input); input = IntPtr.Zero; }
            pcm.Dispose();
        }
    }
}
