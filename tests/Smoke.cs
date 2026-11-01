using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace SpaceDictation {
    // Start on a private desktop. Use a test clipboard because desktops share a system clipboard.
    static class Smoke {
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool GetUserObjectInformation(IntPtr obj, int index, StringBuilder text, uint length, out uint needed);
        [DllImport("user32.dll")] static extern IntPtr GetThreadDesktop(uint thread);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        const string Transcript = "请在 GitHub 上打开这个 pull request。";
        static void Check(bool ok, string description) {
            if (!ok) throw new Exception(description + " failed, Win32 " + Marshal.GetLastWin32Error());
            Console.WriteLine("OK  " + description);
        }
        static void Pump(int milliseconds) {
            var time = Stopwatch.StartNew();
            while (time.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(5); }
        }
        static object Field(object target, string name) { return target.GetType().GetField(name, Hidden).GetValue(target); }
        static void Key(SpaceKey hook, int key, bool down) {
            IntPtr buffer = Marshal.AllocHGlobal(32);
            try {
                Marshal.Copy(new byte[32], 0, buffer, 32);
                Marshal.WriteInt32(buffer, key); Marshal.WriteInt32(buffer, 8, down ? 0 : 128);
                typeof(SpaceKey).GetMethod("Handle", Hidden).Invoke(hook, new object[] { 0, IntPtr.Zero, buffer });
            } finally { Marshal.FreeHGlobal(buffer); }
        }
        static HttpResponseMessage Reply(string code, string body) {
            var reply = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
            reply.Headers.Add("X-Api-Status-Code", code); return reply;
        }
        sealed class Fixture : HttpMessageHandler {
            public bool silent, fail;
            public int calls;
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage msg, CancellationToken cancellation) {
                calls++;
                var serializer = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
                var doc = (Dictionary<string, object>)serializer.DeserializeObject(await msg.Content.ReadAsStringAsync());
                var options = (Dictionary<string, object>)doc["request"];
                var audio = (Dictionary<string, object>)doc["audio"];
                var wave = Convert.FromBase64String((string)audio["data"]);
                Check(msg.RequestUri.Host == "openspeech.bytedance.com" && msg.Headers.GetValues("X-Api-Resource-Id").Single() == "volc.bigasr.auc_turbo", "official endpoint and resource");
                Check(msg.Headers.GetValues("X-Api-Key").Single() == "fixture" && msg.Headers.GetValues("X-Api-Sequence").Single() == "-1", "auth and sequence");
                Check(wave.Length > 3244 && BitConverter.ToInt32(wave, 24) == 16000 && BitConverter.ToInt32(wave,40) == wave.Length - 44, "actual microphone produced 16 kHz WAV in memory");
                Check((bool)options["enable_ddc"] && (bool)options["enable_punc"] && !audio.ContainsKey("language"), "smoothing and punctuation, no forced language");
                Check(((string)((Dictionary<string, object>)options["corpus"])["context"]).Contains("GitHub"), "hotwords serialized for provider");
                if (fail) return new HttpResponseMessage(HttpStatusCode.Forbidden);
                return silent ? Reply("20000003", "{}") : Reply("20000000", serializer.Serialize(new { result = new { text = Transcript } }));
            }
        }
        [STAThread] public static int Main(string[] args) {
            try {
                var historyRoot = Path.Combine(Path.GetTempPath(), "QingtingSmoke", Guid.NewGuid().ToString("N"));
                var history = new HistoryStore(historyRoot);
                if (args.Length == 1 && args[0] == "--paste-check") {
                    using (var editor = new Form { Text = "轻听 · 自动粘贴验证", TopMost = true })
                    using (var first = new TextBox { Dock = DockStyle.Top })
                    using (var second = new TextBox { Dock = DockStyle.Bottom }) {
                        editor.Controls.Add(first); editor.Controls.Add(second);
                        editor.Show(); editor.Activate(); first.Focus(); Pump(200);
                        var focusWait = Stopwatch.StartNew();
                        while (GetForegroundWindow() != editor.Handle && focusWait.ElapsedMilliseconds < 25000) Pump(100);
                        first.Focus(); Pump(100);
                        Check(GetForegroundWindow() == editor.Handle && first.Focused, "the test owns keyboard focus");
                        var target = PasteTarget.Capture();
                        Wait(target);
                        if (target.Result == null) {
                            var element = System.Windows.Automation.AutomationElement.FocusedElement;
                            Console.WriteLine("Test form active: " + (Form.ActiveForm == editor) + ", field focused: " + first.Focused
                                + ", UIA type: " + (element == null ? "none" : element.Current.ControlType.ProgrammaticName));
                        }
                        Check(target.Result != null, "captures editable input");
                        Clipboard.SetText(Transcript);
                        var paste = PasteTarget.PasteIfCurrent(target, PasteTarget.GetClipboardSequenceNumber(), expectedText: Transcript);
                        Wait(paste); Pump(200);
                        Console.WriteLine("paste sent=" + paste.Result + " / received length=" + first.Text.Length + " / expected=" + Transcript.Length + " / clipboard matches=" + (Clipboard.GetText() == Transcript));
                        Check(paste.Result && first.Text == Transcript && Clipboard.GetText() == Transcript, "real Ctrl+V inserts the clipboard content once (" + PasteTarget.LastReason + ")");
                        target = PasteTarget.Capture(); Wait(target); second.Focus(); Pump(80);
                        paste = PasteTarget.PasteIfCurrent(target, PasteTarget.GetClipboardSequenceNumber());
                        Wait(paste); Pump(80);
                        Check(!paste.Result && second.Text.Length == 0, "changing input in the same window skips paste");
                        second.UseSystemPasswordChar = true; Pump(80);
                        target = PasteTarget.Capture(); Wait(target); Check(target.Result == null, "password field is excluded");
                        string longText = String.Concat(Enumerable.Repeat("语料 GitHub\r\n", 500));
                        DateTimeOffset now = DateTimeOffset.Now;
                        history.Add(now.AddDays(-1), "昨日的记录");
                        history.Add(now, longText);
                        history = new HistoryStore(historyRoot);
                        int skipped;
                        Check(history.Days().Length == 2 && history.Read(now.ToString("yyyy-MM-dd"), out skipped).Single().Text == longText, "two dates and full multiline text survive reopening");
                        first.Multiline = true; first.MaxLength = 0; first.Clear(); first.Focus(); Pump(100);
                        target = PasteTarget.Capture(true); Wait(target);
                        using (var widget = new Widget(null, history))
                        using (var timer = new System.Windows.Forms.Timer { Interval = 100 }) {
                            var handle = widget.Handle;
                            bool copied = false;
                            timer.Tick += delegate {
                                var dialog = Application.OpenForms.OfType<HistoryWindow>().FirstOrDefault();
                                if (dialog == null) return;
                                timer.Stop();
                                ((Button)Field(dialog,"copy")).PerformClick();
                                copied = Clipboard.GetText() == longText;
                                var button = (Button)Field(dialog,"paste");
                                if (button.Enabled) button.PerformClick(); else dialog.Close();
                            };
                            timer.Start();
                            typeof(Widget).GetMethod("OpenHistory", Hidden).Invoke(widget, new object[] { target });
                            Pump(1800);
                            Check(copied && first.Text == longText, "history buttons copy full text and paste back into the original input");
                        }
                    }
                    return 0;
                }
                if (args.Length == 2 && args[0] == "--local-clipboard") {
                    using (var offline = new Widget(null, history)) {
                        var handle = offline.Handle; // Hidden form on the interactive desktop, no global hook.
                        var settings = (Preferences)Field(offline, "prefs");
                        settings.UseLocal = true; settings.Key = ""; settings.Words = "GitHub, Codex, ChatGPT, Google";
                        typeof(Widget).GetMethod("Submit", Hidden).Invoke(offline, new object[] { File.ReadAllBytes(args[1]) });
                        var wait = Stopwatch.StartNew();
                        while ((bool)Field(offline, "busy") && wait.Elapsed.TotalSeconds < 45) Pump(40);
                        string result = (string)Field(offline, "lastText");
                        Console.WriteLine("Offline fixture: " + result + " / " + (string)Field(offline, "error"));
                        Check(result.Contains("GitHub") && result.Contains("代码"), "real mixed-language offline inference");
                        Check(((string)Field(offline, "title")).StartsWith("已复制") && Clipboard.GetText() == result, "system clipboard matches model output");
                        Console.WriteLine("Offline result: " + result);
                        offline.Close();
                        return 0;
                    }
                }
                var name = new StringBuilder(128); uint needed;
                Check(GetUserObjectInformation(GetThreadDesktop(GetCurrentThreadId()), 2, name, 256, out needed)
                    && name.ToString().StartsWith("SpaceDictationTest_"), "isolated desktop");
                Application.EnableVisualStyles();
                var keys = new List<string>(); int starts = 0, stops = 0;
                using (var hook = new SpaceKey(() => true, () => starts++, () => stops++, (k,s,f,e) => keys.Add(k + "/" + f))) {
                    Key(hook, 32, true); Key(hook, 32, false);
                    Check(String.Join(",", keys) == "32/0,32/2" && starts == 0, "short space replays one tap");
                    keys.Clear(); Key(hook, 32, true); Key(hook, 65, true); Key(hook, 32, false);
                    Check(String.Join(",", keys) == "32/0,32/2,65/0", "fast rollover preserves space-letter order");
                    keys.Clear(); Key(hook, 32, true); Pump(420); Key(hook, 32, true); Key(hook, 32, false);
                    Check(keys.Count == 0 && starts == 1 && stops == 1, "hold and repeat start/stop exactly once");
                }
                var fixture = new Fixture();
                typeof(Doubao).GetField("client", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, new HttpClient(fixture));
                string contents = "";
                Action<string> write = text => contents = text;
                Func<string> read = () => contents;
                using (var form = new Widget(write, history)) {
                    form.Show(); Pump(100);
                    var p = (Preferences)Field(form, "prefs"); p.Key = "fixture"; p.UseLocal = false; p.GlobalSpace = true;
                    if (args.Length > 0) {
                        typeof(Widget).GetMethod("Idle", Hidden).Invoke(form, null);
                        using (var bitmap = new Bitmap(form.Width, form.Height)) {
                            form.DrawToBitmap(bitmap, form.ClientRectangle); bitmap.Save(args[0]);
                        }
                    }
                    using (var settings = new Settings(p)) {
                        settings.Show(); Pump(100);
                        if (args.Length > 1) using (var shot = new Bitmap(settings.Width, settings.Height)) {
                            settings.DrawToBitmap(shot, new Rectangle(Point.Empty, settings.Size)); shot.Save(args[1]);
                        }
                        settings.Close();
                    }
                    var hook = (SpaceKey)Field(form, "hook");
                    write("initial");
                    Action dictate = delegate { Key(hook, 32, true); Pump(1150); Key(hook, 32, false); Pump(500); };
                    dictate();
                    Check(fixture.calls == 1 && read() == Transcript, "hold to record, release to transcribe, Unicode clipboard output");
                    fixture.silent = true; write("before silence"); dictate();
                    Check(fixture.calls == 2 && read() == "before silence", "silence keeps existing clipboard");
                    fixture.silent = false; fixture.fail = true; dictate();
                    Check(fixture.calls == 3 && read() == "before silence" && Field(form,"retryAudio") != null, "failure retains recording and clipboard");
                    fixture.fail = false;
                    typeof(Widget).GetMethod("Submit", Hidden).Invoke(form, new[] { Field(form, "retryAudio") });
                    Pump(500);
                    Check(fixture.calls == 4 && read() == Transcript && Field(form,"retryAudio") == null, "retry copies recovered text");
                    int skipped;
                    Check(history.Read(DateTime.Now.ToString("yyyy-MM-dd"), out skipped).Count == 2,
                        "each successful dictation is stored once, including retry");
                    form.Close();

                }
                return 0;
            } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        static void Wait(Task task) {
            var timer = Stopwatch.StartNew();
            while (!task.IsCompleted && timer.ElapsedMilliseconds < 5000) Pump(20);
            if (!task.IsCompleted) throw new TimeoutException("Focus check timed out");
        }
    }
}
