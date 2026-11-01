using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace SpaceDictation {
    internal sealed class HistoryEntry {
        public string Time { get; set; }
        public string Text { get; set; }
        public override string ToString() {
            DateTimeOffset time;
            DateTimeOffset.TryParse(Time, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out time);
            string preview = Text.Substring(0, Math.Min(100, Text.Length)).Replace('\r',' ').Replace('\n',' ');
            return time.ToString("HH:mm:ss") + "     " + preview;
        }
    }
    internal sealed class HistoryStore {
        readonly string root;
        public HistoryStore(string folder = null) {
            root = folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpaceDictation", "History");
        }
        public void Add(DateTimeOffset time, string text) {
            var folder = Path.Combine(root, time.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, time.UtcDateTime.Ticks.ToString("D19") + "-" + Guid.NewGuid().ToString("N"));
            var entry = new HistoryEntry { Time = time.ToString("o"), Text = text };
            try {
                File.WriteAllText(path + ".tmp", new JavaScriptSerializer().Serialize(entry), new UTF8Encoding(false));
                File.Move(path + ".tmp", path + ".json");
            } finally { if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp"); }
        }
        public string[] Days() {
            var names = Directory.Exists(root) ? Directory.GetDirectories(root).Select(Path.GetFileName) : Enumerable.Empty<string>();
            return names.Where(s => { DateTime d; return DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d); })
                .Concat(new[] { DateTime.Today.ToString("yyyy-MM-dd") }).Distinct().OrderByDescending(s => s, StringComparer.Ordinal).ToArray();
        }
        public List<HistoryEntry> Read(string day, out int unreadable) {
            unreadable = 0;
            var items = new List<HistoryEntry>();
            var dir = Path.Combine(root, day);
            if (!Directory.Exists(dir)) return items;
            foreach (string file in Directory.GetFiles(dir, "*.json").OrderByDescending(x => x, StringComparer.Ordinal)) {
                try {
                    var record = new JavaScriptSerializer().Deserialize<HistoryEntry>(File.ReadAllText(file, Encoding.UTF8));
                    if (record == null || String.IsNullOrWhiteSpace(record.Text)) { unreadable++; continue; }
                    items.Add(record);
                } catch (Exception) { unreadable++; }
            }
            return items;
        }
    }
    internal sealed class HistoryWindow : Form {
        readonly ListBox days = new ListBox(), entries = new ListBox();
        readonly TextBox detail = new TextBox { ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical };
        readonly Label status = new Label { AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
        readonly Button copy = new Button { Text = "复制全文" }, paste = new Button { Text = "粘贴到原输入框" };
        readonly HistoryStore store;
        readonly bool hasTarget;
        public string PasteText { get; private set; }
        public HistoryWindow(HistoryStore store, bool hasTarget) {
            this.store = store; this.hasTarget = hasTarget;
            Text = "轻听 · 听写历史"; Font = new Font("Microsoft YaHei UI", 9);
            AutoScaleDimensions = new SizeF(96,96); AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterScreen; ClientSize = new Size(720,460); MinimumSize = new Size(560,380);
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            BackColor = Color.FromArgb(247,249,247);
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 2, RowCount = 4 };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,135)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute,32)); grid.RowStyles.Add(new RowStyle(SizeType.Percent,45));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent,55)); grid.RowStyles.Add(new RowStyle(SizeType.Absolute,42));
            Controls.Add(grid);
            grid.Controls.Add(new Label { Text = "记录日期", AutoSize = true },0,0);
            grid.Controls.Add(new Label { Text = "听写时间与原文", AutoSize = true },1,0);
            days.Dock = entries.Dock = detail.Dock = DockStyle.Fill;
            days.IntegralHeight = entries.IntegralHeight = false;
            days.AccessibleName = "记录日期"; entries.AccessibleName = "当天听写"; detail.AccessibleName = "所选听写全文";
            grid.Controls.Add(days,0,1); grid.SetRowSpan(days,2); grid.Controls.Add(entries,1,1); grid.Controls.Add(detail,1,2);
            var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Margin = Padding.Empty };
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,95));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,145)); grid.Controls.Add(actions,0,3); grid.SetColumnSpan(actions,2);
            copy.Dock = paste.Dock = status.Dock = DockStyle.Fill;
            actions.Controls.Add(status,0,0); actions.Controls.Add(copy,1,0); actions.Controls.Add(paste,2,0);
            copy.Click += delegate {
                try { Clipboard.SetDataObject(((HistoryEntry)entries.SelectedItem).Text, true, 6, 60); status.Text = "全文已复制"; }
                catch (Exception) { status.Text = "剪贴板正忙，请重试"; }
            };
            paste.Click += delegate { PasteText = ((HistoryEntry)entries.SelectedItem).Text; DialogResult = DialogResult.OK; };
            entries.SelectedIndexChanged += delegate {
                var record = entries.SelectedItem as HistoryEntry; detail.Text = record == null ? "" : record.Text;
                copy.Enabled = record != null; paste.Enabled = record != null && hasTarget;
            };
            days.SelectedIndexChanged += delegate { ShowDay(); };
            days.Items.AddRange(store.Days()); days.SelectedIndex = 0;
        }
        void ShowDay() {
            entries.Items.Clear(); detail.Clear(); copy.Enabled = paste.Enabled = false;
            try {
                int skipped; var rows = store.Read((string)days.SelectedItem, out skipped);
                entries.Items.AddRange(rows.ToArray());
                if (rows.Count > 0) entries.SelectedIndex = 0;
                status.Text = rows.Count == 0 ? "这一天还没有听写记录" : rows.Count + " 条记录";
                if (skipped > 0) status.Text += " · " + skipped + " 条无法读取";
                else if (!hasTarget && rows.Count > 0) status.Text += " · 可复制后手动粘贴";
            } catch (Exception) { status.Text = "无法读取当天记录，请检查本机文件权限"; }
        }
    }
}
