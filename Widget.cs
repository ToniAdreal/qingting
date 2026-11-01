using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Threading;

namespace SpaceDictation {
    internal sealed class Widget : Form {
        readonly System.Windows.Forms.Timer pulse = new System.Windows.Forms.Timer { Interval = 80 };
        readonly Stopwatch recordingTime = new Stopwatch();
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly Font heading = new Font("Microsoft YaHei UI", 11, FontStyle.Bold, GraphicsUnit.Pixel);
        readonly Font caption = new Font("Microsoft YaHei UI", 11, FontStyle.Regular, GraphicsUnit.Pixel);
        readonly Color mint = Color.FromArgb(131, 232, 196);
        readonly Color coral = Color.FromArgb(255, 149, 125);
        readonly Action<string> writeClipboard;
        readonly LocalWhisper local = new LocalWhisper();
        Preferences prefs;
        SpaceKey hook;
        Recorder recorder;
        bool busy, settingsOpen, fromKey, dragging, pressed, closing;
        Point origin, mouseOrigin;
        float scale;
        string title, hint, lastText = "", error = "";
        byte[] retryAudio;
        Task<PasteTarget> pasteTarget;
        PasteGuard pasteGuard;
        Color accent;
        int strength;
        public Widget(Action<string> writeClipboard = null) {
            this.writeClipboard = writeClipboard ?? (text => Clipboard.SetDataObject(text, true, 6, 60));
            Text = "轻听 · 空格听写";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = true; TopMost = true;
            DoubleBuffered = true; StartPosition = FormStartPosition.Manual;
            using (var g = CreateGraphics()) scale = g.DpiX / 96f;
            ClientSize = new Size((int)(252 * scale), (int)(62 * scale));
            var work = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(work.Right - Width - 20, work.Bottom - Height - 20);
            using (var shape = Round(ClientRectangle, 18 * scale)) Region = new Region(shape);
            try { prefs = Preferences.Read(); }
            catch { prefs = new Preferences(); error = "旧设置无法读取，请重新填写并保存。"; }
            Shown += delegate {
                try { hook = new SpaceKey(CanUseSpace, delegate { StartRecording(true); },
                    delegate { BeginInvoke((Action)delegate { if (fromKey) FinishRecording(); }); },
                    otherKey: delegate { if (pasteGuard != null) pasteGuard.Cancel(); }); }
                catch { error = "Windows 未能启用全局空格，可点击悬浮窗开始和结束录音。"; }
                Idle();
            };
            pulse.Tick += delegate {
                try {
                    recorder.Drain(true);
                    strength = Math.Min(15, (int)Math.Sqrt(recorder.Peak) / 6);
                    hint = recordingTime.Elapsed.ToString(@"mm\:ss") + (fromKey ? "   松开空格完成" : "   再点一次完成");
                    if (recordingTime.Elapsed.TotalMinutes >= 5) FinishRecording();
                    Invalidate();
                } catch (Exception ex) { EndPasteWatch(); DropRecorder(); Fail(ex.Message); }
            };
            menu.Opening += delegate {
                menu.Items.Clear();
                Item("语音与麦克风设置", OpenSettings, !busy && recorder == null);
                Item("重试上一段录音", delegate { Submit(retryAudio); }, retryAudio != null && !busy && recorder == null && Ready);
                if (retryAudio != null) Item("重新录一段", delegate { StartRecording(false); }, !busy && recorder == null && Ready);
                Item("再复制上一段文字", delegate { CopyLast(); }, lastText.Length != 0 && !busy && recorder == null);
                if (error.Length > 0) Item("查看提示", delegate { MessageBox.Show(this, error, Text); }, true);
                menu.Items.Add(new ToolStripSeparator());
                Item("退出轻听", Close, true);
            };
            Idle();
        }
        bool Ready { get { return prefs.UseLocal || !String.IsNullOrWhiteSpace(prefs.Key); } }
        bool CanUseSpace() { return !closing && Ready && prefs.GlobalSpace && !settingsOpen && !menu.Visible && !busy && recorder == null; }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override void WndProc(ref Message m) {
            if (m.Msg == 0x21) { m.Result = (IntPtr)3; return; }  // MA_NOACTIVATE, keep the target input focused
            base.WndProc(ref m);
        }
        void Item(string text, Action action, bool enabled) {
            var item = menu.Items.Add(text); item.Enabled = enabled; item.Click += delegate { action(); };
        }
        static GraphicsPath Round(Rectangle r, float d) {
            var path = new GraphicsPath();
            path.AddArc(r.Left,r.Top,d,d,180,90); path.AddArc(r.Right-d,r.Top,d,d,270,90);
            path.AddArc(r.Right-d,r.Bottom-d,d,d,0,90); path.AddArc(r.Left,r.Bottom-d,d,d,90,90);
            path.CloseFigure(); return path;
        }
        void State(string a, string b, Color color) {
            title = a; hint = b; accent = color; AccessibleName = a + "，" + b; Invalidate();
        }
        void Idle() {
            if (!Ready) State("轻听   /   选择识别方式", "点此设置语音和麦克风", mint);
            else if (hook == null || !prefs.GlobalSpace) State("轻听   /   待命", "点击开始录音", mint);
            else State(prefs.UseLocal ? "轻听   /   本地听写" : "轻听   /   豆包听写", prefs.AutoPaste ? "按住空格说话，松开粘贴" : "按住空格说话，松开复制", mint);
        }
        void Fail(string info) {
            error = info;
            State(retryAudio != null ? "识别失败   /   点此重试" : "需要检查设置", "点右侧菜单查看提示", coral);
        }
        protected override void OnPaint(PaintEventArgs e) {
            var g = e.Graphics; g.Clear(Color.FromArgb(28, 39, 41));
            g.ScaleTransform(scale, scale); g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var disk = new SolidBrush(Color.FromArgb(43, 59, 59))) g.FillEllipse(disk, 10, 10, 42, 42);
            using (var pen = new Pen(accent, 2.6f)) {
                pen.StartCap = pen.EndCap = LineCap.Round;
                int[] heights = { 4, 10, 17, 8, 3 };
                for (int i = 0; i < heights.Length; i++) {
                    float h = heights[i] + (recorder != null ? strength * (i % 2 == 0 ? .4f : .85f) : 0);
                    g.DrawLine(pen, 19 + 6*i, 31 - h/2, 19 + 6*i, 31 + h/2);
                }
            }
            using (var light = new SolidBrush(Color.FromArgb(244, 247, 243)))
            using (var muted = new SolidBrush(Color.FromArgb(182, 200, 193))) {
                g.DrawString(title, heading, light, 61, 12);
                g.DrawString(hint, caption, muted, 61, 33);
                for (int i = 0; i < 3; i++) g.FillEllipse(muted, 228, 22 + 6*i, 3, 3);
            }
        }
        protected override void OnMouseDown(MouseEventArgs e) {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Right || (e.Button == MouseButtons.Left && e.X >= 212*scale)) {
                menu.Show(this, e.Location); return;
            }
            if (e.Button != MouseButtons.Left) return;
            pressed = true; dragging = false; origin = Location; mouseOrigin = Cursor.Position; Capture = true;
        }
        protected override void OnMouseMove(MouseEventArgs e) {
            base.OnMouseMove(e); if (!pressed) return;
            Point pos = Cursor.Position; int dx = pos.X - mouseOrigin.X, dy = pos.Y - mouseOrigin.Y;
            if (Math.Abs(dx) + Math.Abs(dy) > 5*scale) dragging = true;
            if (dragging) {
                var r = Screen.FromPoint(pos).WorkingArea;
                Location = new Point(Math.Max(r.Left, Math.Min(r.Right - Width, origin.X + dx)),
                                     Math.Max(r.Top, Math.Min(r.Bottom - Height, origin.Y + dy)));
            }
        }
        protected override void OnMouseUp(MouseEventArgs e) {
            base.OnMouseUp(e); if (!pressed || e.Button != MouseButtons.Left) return;
            pressed = false; Capture = false; if (dragging || busy || settingsOpen) return;
            if (recorder != null) { if (!fromKey) FinishRecording(); }
            else if (!Ready || title == "豆包模型未开通" || (error.Length > 0 && retryAudio == null && title == "需要检查设置")) OpenSettings();
            else if (retryAudio != null) Submit(retryAudio);
            else StartRecording(false);
        }
        void OpenSettings() {
            if (settingsOpen || busy || recorder != null) return;
            settingsOpen = true;
            State("快捷键已暂停", "关闭设置窗口后可长按空格", mint);
            try {
                using (var window = new Settings(prefs))
                    if (window.ShowDialog(this) == DialogResult.OK) prefs = window.Value;
            } finally { settingsOpen = false; }
            if (retryAudio != null && Ready) State("上一段录音   /   点此重试", "也可长按空格录新的一段", mint);
            else Idle();
        }
        void StartRecording(bool keyboard) {
            if (busy || recorder != null || settingsOpen || !Ready || closing) return;
            try {
                EndPasteWatch();
                if (keyboard && prefs.AutoPaste) pasteGuard = new PasteGuard();
                pasteTarget = keyboard && prefs.AutoPaste ? PasteTarget.Capture() : null;
                recorder = new Recorder(prefs.Device);
                retryAudio = null; error = ""; fromKey = keyboard;
                State("正在聆听", keyboard ? "松开空格完成" : "再点一次完成", coral);
                recordingTime.Restart(); pulse.Start();
            } catch (Exception ex) { EndPasteWatch(); Fail(ex.Message); }
        }
        void EndPasteWatch() {
            pasteTarget = null;
            if (pasteGuard != null) pasteGuard.Dispose();
            pasteGuard = null;
        }
        void DropRecorder() {
            pulse.Stop(); fromKey = false; strength = 0; recordingTime.Stop();
            if (recorder != null) { recorder.Dispose(); recorder = null; }
        }
        void FinishRecording() {
            if (recorder == null) return;
            byte[] audio;
            try { audio = recorder.Finish(); }
            catch (Exception ex) { EndPasteWatch(); Fail(ex.Message); return; }
            finally { DropRecorder(); }
            if (audio.Length < 44 + 3200) { EndPasteWatch(); State("录音太短", "请等到显示「正在聆听」再开口", coral); return; }
            Submit(audio);
        }
        async void Submit(byte[] audio) {
            if (audio == null || busy || closing) return;
            var destination = pasteTarget;
            pasteTarget = null;
            busy = true; retryAudio = audio; error = "";
            State(prefs.UseLocal ? "本机正在转写" : "豆包正在转写", "完成后自动复制整段文字", mint);
            try {
                string text = await Task.Run(() => prefs.UseLocal ? local.Recognize(prefs.Words, audio) : Doubao.Recognize(prefs, audio));
                if (closing) return;
                retryAudio = null;
                if (String.IsNullOrWhiteSpace(text)) { State("没有听清", "请检查麦克风，再说一次", coral); return; }
                lastText = text;
                if (CopyLast() && destination != null &&
                    await PasteTarget.PasteIfCurrent(destination, PasteTarget.GetClipboardSequenceNumber(), () => pasteGuard != null && pasteGuard.Untouched, text) && !closing)
                    State("已复制   /   已触发粘贴", "检查输入框里的文字后发送", mint);
            } catch (UnauthorizedAccessException ex) {
                if (!closing && prefs.UseLocal) Fail(ex.Message);
                else if (!closing) { error = ex.Message; State("豆包模型未开通", "点此切换本地或查看设置", coral); }
            } catch (TaskCanceledException) { if (!closing) Fail("连接超时。点击悬浮窗可重试这段录音。"); }
            catch (HttpRequestException) { if (!closing) Fail("无法连接豆包，请检查网络或代理。点击悬浮窗重试。"); }
            catch (Exception ex) { if (!closing) Fail(ex.Message); }
            finally { EndPasteWatch(); busy = false; }
        }
        bool CopyLast() {
            try {
                writeClipboard(lastText);
                State("已复制   /   Ctrl + V", "到输入框粘贴，回车发送", mint);
                return true;
            } catch (Exception) {
                error = "文字已保留，请从右侧菜单选择「再复制上一段文字」。";
                State("剪贴板正忙", "点菜单，再复制上一段文字", coral);
                return false;
            }
        }
        protected override void OnFormClosed(FormClosedEventArgs e) {
            closing = true; EndPasteWatch(); if (hook != null) hook.Dispose(); DropRecorder(); pulse.Dispose(); local.Dispose();
            menu.Dispose(); heading.Dispose(); caption.Dispose(); base.OnFormClosed(e);
        }
    }
    internal static class Program {
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        [STAThread] static void Main() {
            bool first;
            using (var mutex = new Mutex(true, @"Local\SpaceDictation.Widget.v1", out first)) {
                if (!first) return;
                SetProcessDPIAware(); Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                Application.Run(new Widget());
            }
        }
    }
}
