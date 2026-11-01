using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace SpaceDictation {
    internal sealed class Preferences {
        public string Key { get; set; }
        public string Device { get; set; }
        public string Words { get; set; }
        public bool GlobalSpace { get; set; }
        public bool UseLocal { get; set; }
        public bool AutoPaste { get; set; }
        public Preferences() {
            Key = ""; Device = ""; Words = "GitHub, Codex, ChatGPT, Google"; GlobalSpace = true; AutoPaste = true;
        }
        static string FilePath {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpaceDictation", "settings.dat"); }
        }
        public static Preferences Read() {
            if (!File.Exists(FilePath)) return new Preferences();
            byte[] bytes = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), null, DataProtectionScope.CurrentUser);
            return new JavaScriptSerializer().Deserialize<Preferences>(Encoding.UTF8.GetString(bytes));
        }
        public void Save() {
            var bytes = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(this));
            var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            string part = FilePath + ".new";
            File.WriteAllBytes(part, encrypted);
            if (File.Exists(FilePath)) File.Replace(part, FilePath, null); else File.Move(part, FilePath);
        }
    }
    internal static class Doubao {
        const string Endpoint = "https://openspeech.bytedance.com/api/v3/auc/bigmodel/recognize/flash";
        const string Resource = "volc.bigasr.auc_turbo";
        static readonly HttpClient client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) {
            Timeout = TimeSpan.FromSeconds(90)
        };
        static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 }; }
        internal static HttpRequestMessage Request(Preferences p, byte[] wave) {
            var serializer = Json();
            var options = new Dictionary<string, object> {
                { "model_name", "bigmodel" }, { "enable_itn", true }, { "enable_punc", true }, { "enable_ddc", true }
            };
            var words = (p.Words ?? "").Split(new[] { ',', '，', '、', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (words.Length > 0)
                options["corpus"] = new { context = serializer.Serialize(new { hotwords = words.Select(w => new { word = w }).ToArray() }) };
            var body = new {
                user = new { uid = "space-dictation" },
                audio = new { format = "wav", rate = 16000, bits = 16, channel = 1, data = Convert.ToBase64String(wave) },
                request = options
            };
            var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            request.Headers.Add("X-Api-Key", p.Key);
            request.Headers.Add("X-Api-Resource-Id", Resource);
            request.Headers.Add("X-Api-Request-Id", Guid.NewGuid().ToString());
            request.Headers.Add("X-Api-Sequence", "-1");
            request.Content = new StringContent(serializer.Serialize(body), Encoding.UTF8, "application/json");
            return request;
        }
        internal static string ReadResult(HttpResponseMessage response, string body) {
            IEnumerable<string> values;
            var code = response.Headers.TryGetValues("X-Api-Status-Code", out values) ? values.FirstOrDefault() : null;
            if (code == "20000003" || code == "45000002") return "";
            if (!response.IsSuccessStatusCode || code != "20000000") {
                int http = (int)response.StatusCode;
                if (http == 403 && code == "45000030" && body.IndexOf("resource not granted", StringComparison.OrdinalIgnoreCase) >= 0)
                    throw new UnauthorizedAccessException("当前项目未开通录音文件识别。可切换为本地听写，或在豆包控制台中开通「录音文件识别2.0」。");
                string reason = http == 401 || http == 403 ? "请核对语音 API Key，并确认同一项目已开通极速版。"
                    : http == 429 || code == "55000031" ? "服务正忙或额度受限。请稍后重试并检查控制台用量。"
                    : "请检查网络、服务开通情况和控制台用量。";
                throw new IOException(reason + "（HTTP " + http + " / " + (code ?? "未返回服务码") + "）");
            }
            var root = Json().DeserializeObject(body) as Dictionary<string, object>;
            object result, value;
            if (root == null || !root.TryGetValue("result", out result) || !(result is Dictionary<string, object>) ||
                !((Dictionary<string, object>)result).TryGetValue("text", out value) || !(value is string))
                throw new InvalidDataException("豆包返回了无法读取的识别结果。");
            return ((string)value).Trim();
        }
        public static async Task<string> Recognize(Preferences p, byte[] wave) {
            using (var request = Request(p, wave))
            using (var response = await client.SendAsync(request).ConfigureAwait(false))
                return ReadResult(response, await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        }
    }
}
