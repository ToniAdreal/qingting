using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace SpaceDictation {
    internal sealed class Settings : Form {
        readonly TextBox key = new TextBox { UseSystemPasswordChar = true };
        readonly TextBox words = new TextBox { Multiline = true, MaxLength = 450, ScrollBars = ScrollBars.Vertical };
        readonly ComboBox devices = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        readonly CheckBox global = new CheckBox { Text = "在其他应用中也可以长按空格听写", AutoSize = true };
        readonly Button test = new Button { Text = "测试麦克风" };
        readonly ProgressBar level = new ProgressBar { Maximum = 100 };
        readonly Label testHint = new Label();
        readonly Timer meter = new Timer { Interval = 100 };
        readonly Stopwatch clock = new Stopwatch();
        Recorder preview;
        bool signal;
        public Preferences Value { get; private set; }
        public Settings(Preferences current) {
            Text = "轻听 · 设置"; Font = new Font("Microsoft YaHei UI", 9F);
            AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen; TopMost = true;
            BackColor = Color.FromArgb(249, 250, 248); ClientSize = new Size(454, 486);
            LabelAt("识别方式", 18, 15, 122, 30).Font = new Font(Font.FontFamily, 14, FontStyle.Bold);
            var engine = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            engine.Items.AddRange(new object[] { "本地 Whisper（离线）", "在线豆包" });
            Put(engine, 159, 16, 276, 26);
            var engineHint = LabelAt("", 19, 51, 419, 22);
            LabelAt("语音 API Key", 19, 88, 140, 20);
            var link = new LinkLabel { Text = "打开豆包语音控制台", TextAlign = ContentAlignment.TopRight };
            Put(link, 244, 88, 190, 22);
            link.LinkClicked += delegate { Process.Start("https://console.volcengine.com/speech/new/setting/apikeys?projectName=default"); };
            key.Text = current.Key; Put(key, 19, 113, 416, 24);
            var cloudHint = LabelAt("在线模式需在同一项目开通「录音文件识别2.0」。", 19, 142, 416, 21);
            engine.SelectedIndexChanged += delegate {
                bool offline = engine.SelectedIndex == 0;
                engineHint.Text = offline ? "在本机识别中英文。首次使用时载入已有模型。" : "录音结束后发送到火山引擎，按账号额度或用量计费。";
                key.Enabled = link.Enabled = cloudHint.Visible = !offline;
            };
            engine.SelectedIndex = current.UseLocal ? 0 : 1;
            LabelAt("录音输入", 19, 179, 100, 22);
            Put(devices, 19, 204, 299, 26);
            devices.Items.Add("跟随 Windows 默认输入");
            foreach (var name in Recorder.Devices().Values) devices.Items.Add(name);
            int selected = String.IsNullOrEmpty(current.Device) ? 0 : devices.Items.IndexOf(current.Device);
            if (selected < 0) selected = devices.Items.Add(current.Device);
            devices.SelectedIndex = selected;
            Put(test, 325, 202, 110, 29);
            Put(level, 19, 239, 125, 12);
            Put(testHint, 154, 234, 280, 23); testHint.Text = "只测试本机音量。";
            test.Click += delegate { if (preview == null) StartTest(); else EndTest(); };
            meter.Tick += delegate {
                try {
                    preview.Drain(true);
                    level.Value = Math.Min(100, (int)Math.Sqrt(preview.Peak) * 2);
                    signal |= preview.Peak > 100;
                    if (clock.Elapsed.TotalSeconds >= 5) EndTest();
                } catch (Exception ex) { EndTest(); MessageBox.Show(this, ex.Message, Text); }
            };
            LabelAt("常用词（用逗号或换行分开）", 19, 273, 416, 20);
            words.Text = current.Words; Put(words, 19, 298, 416, 49);
            global.Checked = current.GlobalSpace; Put(global, 19, 368, 415, 25);
            LabelAt("保存并关闭设置后，单独按住空格约 0.35 秒。", 19, 392, 415, 22);
            var paste = new CheckBox { Text = "松开空格后，自动粘贴到开始录音时的输入框", AutoSize = true, Checked = current.AutoPaste };
            Put(paste, 19, 412, 415, 25);
            var save = new Button { Text = "保存设置", BackColor = Color.FromArgb(20, 100, 83), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            Put(save, 325, 444, 110, 32);
            save.Click += delegate {
                string token = key.Text.Trim();
                foreach (char c in token) if (c < 33 || c > 126) {
                    MessageBox.Show(this, "请粘贴控制台中的完整 API Key。", Text); return;
                }
                var value = new Preferences { Key = token, Device = SelectedDevice(), Words = words.Text.Trim(), GlobalSpace = global.Checked, UseLocal = engine.SelectedIndex == 0, AutoPaste = paste.Checked };
                try { value.Save(); Value = value; DialogResult = DialogResult.OK; }
                catch (Exception ex) { MessageBox.Show(this, "设置保存失败。" + ex.Message, Text); }
            };
            var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel };
            Put(cancel, 235, 444, 80, 32); CancelButton = cancel;
        }
        string SelectedDevice() { return devices.SelectedIndex == 0 ? "" : (string)devices.SelectedItem; }
        void Put(Control c, int x, int y, int w, int h) { c.SetBounds(x,y,w,h); Controls.Add(c); }
        Label LabelAt(string text, int x, int y, int w, int h) {
            var label = new Label { Text = text }; Put(label, x,y,w,h); return label;
        }
        void StartTest() {
            try {
                preview = new Recorder(SelectedDevice()); signal = false;
                test.Text = "停止测试"; devices.Enabled = false; testHint.Text = "说一句话，观察左侧音量。";
                clock.Restart(); meter.Start();
            } catch (Exception ex) { MessageBox.Show(this, ex.Message, Text); }
        }
        void EndTest() {
            meter.Stop(); if (preview != null) preview.Dispose(); preview = null;
            devices.Enabled = true; test.Text = "测试麦克风"; level.Value = 0;
            testHint.Text = signal ? "已收到麦克风声音。" : "音量太低，请检查输入或切换设备。";
        }
        protected override void OnFormClosed(FormClosedEventArgs e) {
            EndTest(); meter.Dispose(); base.OnFormClosed(e);
        }
    }
}
