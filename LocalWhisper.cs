using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace SpaceDictation {
    internal sealed class LocalWhisper : IDisposable {
        readonly object gate = new object();
        Process worker;
        Timer idle;
        bool closed;

        Process GetWorker() {
            lock (gate) {
                if (closed) throw new ObjectDisposedException("LocalWhisper");
                if (idle != null) { idle.Dispose(); idle = null; }
                if (worker != null && !worker.HasExited) return worker;
                StopLocked();
                var script = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "local_whisper.py");
                if (!File.Exists(script)) throw new FileNotFoundException("本地听写组件缺失，请将 local_whisper.py 放在轻听程序旁。");
                const string pythonPath = @"\Software\Python\PythonCore\3.12\InstallPath";
                var python = (string)(Microsoft.Win32.Registry.GetValue("HKEY_CURRENT_USER" + pythonPath, "ExecutablePath", null)
                    ?? Microsoft.Win32.Registry.GetValue("HKEY_LOCAL_MACHINE" + pythonPath, "ExecutablePath", null));
                if (!File.Exists(python)) throw new FileNotFoundException("未找到安装 faster-whisper 的 Python 3.12。");
                var start = new ProcessStartInfo {
                    FileName = python, Arguments = "-u \"" + script + "\"",
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8
                };
                start.EnvironmentVariables["PYTHONUTF8"] = "1";
                worker = Process.Start(start);
                worker.BeginErrorReadLine();
                return worker;
            }
        }
        public void WarmUp() {
            // Python loads the model in its own process while the microphone is recording.
            try { GetWorker(); } catch (Exception) { } // Recognize retries and reports the error with the audio intact.
        }
        public void ReleaseWhenIdle() {
            lock (gate) {
                if (closed || worker == null) return;
                if (idle != null) idle.Dispose();
                Timer lease = null;
                lease = new Timer(delegate { lock (gate) { if (idle == lease) StopLocked(); } },
                                  null, Timeout.Infinite, Timeout.Infinite);
                idle = lease;
                idle.Change(120000, Timeout.Infinite);
            }
        }
        public async Task<string> Recognize(string words, byte[] wave) {
            var json = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 };
            Process current = GetWorker();
            try {
                // Encode the request explicitly. The Windows console code page may not be UTF-8.
                byte[] input = Encoding.UTF8.GetBytes(json.Serialize(new { words, wave = Convert.ToBase64String(wave) }) + "\n");
                await current.StandardInput.BaseStream.WriteAsync(input, 0, input.Length).ConfigureAwait(false);
                await current.StandardInput.BaseStream.FlushAsync().ConfigureAwait(false);
                var read = current.StandardOutput.ReadLineAsync();
                if (await Task.WhenAny(read, Task.Delay(TimeSpan.FromMinutes(4))).ConfigureAwait(false) != read)
                    throw new IOException("本机识别超时，可重试录音。");
                string line = await read.ConfigureAwait(false);
                if (line == null) throw new IOException("本地识别进程已停止，可点击重试。");
                var reply = json.Deserialize<Dictionary<string, string>>(line);
                if (reply.ContainsKey("error")) throw new IOException(reply["error"]);
                string text = reply["text"];
                ReleaseWhenIdle();
                return text;
            } catch {
                lock (gate) { if (worker == current) StopLocked(); }
                throw;
            }
        }
        void StopLocked() {
            if (idle != null) { idle.Dispose(); idle = null; }
            if (worker != null) {
                try { if (!worker.HasExited) worker.Kill(); }
                catch (InvalidOperationException) { } // The interpreter exited first.
                worker.Dispose(); worker = null;
            }
        }
        public void Dispose() { lock (gate) { closed = true; StopLocked(); } }
    }
}
