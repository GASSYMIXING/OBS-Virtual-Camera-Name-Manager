using System;
using System.Collections.Generic;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace OBSVCNameManager
{
    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
#if DEBUG
            if (args.Length == 2 && args[0] == "--live-test-output")
            { File.WriteAllText(args[1], LiveTest.Run(), new UTF8Encoding(true)); return; }
            if (args.Length == 2 && args[0] == "--live-probe-output")
            {
                List<string> probeWarnings;
                var probe = RegistryScanner.Scan(out probeWarnings).Where(RegistryScanner.IsTrustedCandidate).ToList();
                File.WriteAllLines(args[1], probe.Select(e => e.View + "|" + e.ValueName + "|" + e.Data), new UTF8Encoding(true));
                return;
            }
            if (args.Length == 2 && args[0] == "--scan-output")
            {
                List<string> warnings;
                var entries = RegistryScanner.Scan(out warnings);
                File.WriteAllLines(args[1], new[] { "Candidates=" + entries.Count(RegistryScanner.IsTrustedCandidate),
                    "Warnings=" + warnings.Count }, new UTF8Encoding(true));
                return;
            }
            if (args.Length == 2 && args[0] == "--self-test-output")
            { File.WriteAllText(args[1], SelfTest.Run(), new UTF8Encoding(true)); return; }
            if (args.Length == 2 && args[0] == "--permission-output")
            {
                List<string> warnings;
                var checks = RegistryScanner.Scan(out warnings).Where(RegistryScanner.IsTrustedCandidate)
                    .Select(e => e.View + "\t" + e.ValueName + "\t" + RegistryScanner.CheckWriteAccess(e));
                File.WriteAllLines(args[1], checks, new UTF8Encoding(true));
                return;
            }
#endif
            string pendingAction = null, pendingName = null;
            if (args.Length > 0)
            {
                if (args.Length == 5 && args[0] == "--elevated" && args[1] == "--action" &&
                    args[2] == "restore" && args[3] == "--backup-path")
                    pendingAction = "restore";
                else if (args.Length == 7 && args[0] == "--elevated" && args[1] == "--action" &&
                    args[2] == "rename" && args[3] == "--name" && args[5] == "--backup-path")
                { pendingAction = "rename"; pendingName = args[4]; }
                else { MessageBox.Show("无效的启动参数。", "OBS Virtual Camera Name Manager"); return; }
                if (!PrivilegeManager.IsAdministrator())
                { MessageBox.Show("没有获得管理员权限，操作已取消。", "OBS Virtual Camera Name Manager"); return; }
                try { BackupManager.UsePath(pendingAction == "restore" ? args[4] : args[6]); }
                catch (ArgumentException) { MessageBox.Show("无效的备份路径。", "OBS Virtual Camera Name Manager"); return; }
            }
            Logger.Write("app startup version=" + AppVersion.Text + " elevated=" + PrivilegeManager.IsAdministrator());
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(pendingAction, pendingName));
        }
    }

    internal sealed class MainForm : Form
    {
        readonly Label name = new Label(), detected = new Label(), count = new Label(), status = new Label(), backupStatus = new Label();
        readonly TextBox newName = new TextBox();
        readonly Button details = new Button(), rescan = new Button(), rename = new Button(), restore = new Button(), elevate = new Button(), menuButton = new Button();
        List<RegistryEntry> entries = new List<RegistryEntry>();
        List<string> warnings = new List<string>();
        RenameResult lastResult;
        BackupLoad backup = new BackupLoad { State = BackupState.Missing };
        string startupAction, startupName, elevatedAction, elevatedName;
        bool isBusy;

        internal MainForm(string action, string actionName)
        {
            startupAction = action; startupName = actionName;
            Text = "OBS Virtual Camera Name Manager";
            ClientSize = new Size(520, 466);
            MinimumSize = MaximumSize = Size;
            Font = new Font("Segoe UI", 10);
            BackColor = Color.FromArgb(248, 249, 251);
            StartPosition = FormStartPosition.CenterScreen;
            Controls.Add(LabelAt("OBS Virtual Camera\nName Manager", 28, 18, 430, 58, 17, FontStyle.Bold));
            menuButton.Text = "⋯"; menuButton.SetBounds(454, 24, 36, 30);
            menuButton.FlatStyle = FlatStyle.Flat; menuButton.FlatAppearance.BorderSize = 0;
            menuButton.Click += (s, e) => ShowMenu(); Controls.Add(menuButton);
            Controls.Add(LabelAt("当前名称", 30, 88, 460, 23, 10, FontStyle.Regular));
            name.SetBounds(30, 111, 460, 34);
            name.Font = new Font("Segoe UI", 16, FontStyle.Bold);
            name.Text = "正在扫描 OBS Virtual Camera...";
            Controls.Add(name);
            detected.SetBounds(30, 148, 460, 21); detected.ForeColor = Color.FromArgb(90, 99, 111);
            Controls.Add(detected);
            Controls.Add(LabelAt("新名称", 30, 183, 460, 24, 10, FontStyle.Regular));
            newName.SetBounds(30, 211, 460, 30); newName.MaxLength = 64;
            newName.TextChanged += (s, e) => UpdateButtons();
            newName.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { newName.Clear(); e.SuppressKeyPress = true; } };
            Controls.Add(newName);
            rename.Text = "修改名称"; rename.SetBounds(30, 255, 150, 38);
            rename.Click += RenameClicked; Controls.Add(rename);
            restore.Text = "恢复原始名称"; restore.SetBounds(194, 255, 150, 38);
            restore.Click += RestoreClicked; Controls.Add(restore);
            elevate.Text = "以管理员身份继续"; elevate.SetBounds(30, 255, 190, 38);
            elevate.Visible = false; elevate.Click += ElevateClicked; Controls.Add(elevate);
            count.SetBounds(30, 315, 460, 25); Controls.Add(count);
            backupStatus.SetBounds(30, 339, 460, 24); Controls.Add(backupStatus);
            details.Text = "查看详细信息"; details.SetBounds(30, 369, 150, 34);
            details.Click += (s, e) => ShowDetails(); Controls.Add(details);
            rescan.Text = "重新扫描"; rescan.SetBounds(194, 369, 120, 34);
            rescan.Click += (s, e) => ScanAsync(true); Controls.Add(rescan);
            status.SetBounds(30, 415, 460, 42);
            status.ForeColor = Color.FromArgb(75, 85, 99); Controls.Add(status);
            AcceptButton = rename;
            UpdateButtons();
            Shown += (s, e) => ScanAsync(true);
            FormClosing += (s, e) => { if (isBusy) { e.Cancel = true; status.Text = "当前正在修改系统配置，请稍候。"; } };
        }

        static Label LabelAt(string text, int x, int y, int w, int h, float size, FontStyle style)
        { return new Label { Text = text, Bounds = new Rectangle(x, y, w, h), Font = new Font("Segoe UI", size, style) }; }

        void Busy(bool busy)
        {
            isBusy = busy;
            rescan.Enabled = details.Enabled = !busy;
            elevate.Enabled = !busy;
            UpdateButtons();
        }

        List<RegistryEntry> Candidates()
        { return entries.Where(RegistryScanner.IsTrustedCandidate).ToList(); }

        bool HasSafeCandidates()
        {
            var targets = Candidates();
            return targets.Count > 0 && targets.Count <= 4 &&
                targets.Select(e => e.View + "|" + e.Path + "|" + e.ValueName).Distinct(StringComparer.OrdinalIgnoreCase).Count() == targets.Count;
        }

        void UpdateButtons()
        {
            bool found = HasSafeCandidates();
            newName.Enabled = !isBusy && found;
            rename.Enabled = !isBusy && found && newName.Text.Trim().Length > 0 &&
                Candidates().Select(e => e.Data).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1;
            restore.Enabled = !isBusy && found && backup.State == BackupState.Valid;
        }

        void ScanAsync(bool clearResult)
        {
            Busy(true); elevate.Visible = false; rename.Visible = restore.Visible = true;
            status.Text = "正在扫描 OBS Virtual Camera...";
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    List<string> scanWarnings;
                    var scanEntries = RegistryScanner.Scan(out scanWarnings);
                    var load = BackupManager.Load();
                    Logger.Write("scan result count=" + scanEntries.Count + " warnings=" + scanWarnings.Count);
                    BeginInvoke((Action)(() => {
                        entries = scanEntries; warnings = scanWarnings; backup = load;
                        if (clearResult) lastResult = null;
                        ShowScan(); Busy(false);
                        status.Text = load.State == BackupState.Corrupt ? load.Message :
                            !HasSafeCandidates() && Candidates().Count == 0 ? "请确认已安装 OBS Studio，且虚拟摄像头组件可用。" :
                            Candidates().Count > 4 ? "检测到多个候选设备，当前版本暂不支持自动修改。" :
                            Candidates().Select(x => x.Data).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1 ?
                                "检测到相关配置项名称不一致，请查看详细信息。" :
                            scanWarnings.Count == 0 ? "设备扫描完成" : "扫描完成；部分项目无法读取。";
                        if (startupAction != null)
                        {
                            string action = startupAction, requestedName = startupName;
                            startupAction = startupName = null;
                            BeginInvoke((Action)(() => StartAction(action, requestedName)));
                        }
                    }));
                }
                catch (Exception ex)
                { BeginInvoke((Action)(() => { status.Text = "扫描失败：" + ex.Message; Busy(false); })); }
            });
        }

        void ShowScan()
        {
            var names = Candidates().Select(x => x.Data)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            name.Text = !HasSafeCandidates() && Candidates().Count > 4 ? "检测到多个候选设备" :
                names.Count > 1 ? "设备名称状态异常" :
                names.Count == 1 ? names[0] : "未检测到 OBS Virtual Camera";
            detected.Text = names.Count == 1 && HasSafeCandidates() ? "已识别 OBS 虚拟摄像头" :
                names.Count > 1 ? "相关配置项名称不一致" : "";
            count.Text = "找到 " + Candidates().Count + " 个相关配置项";
            backupStatus.Text = backup.State == BackupState.Valid ? "✓ 原始配置已备份" :
                backup.State == BackupState.Corrupt ? "备份损坏，恢复已禁用" : "未找到原始配置备份";
            UpdateButtons();
        }

        void RenameClicked(object sender, EventArgs args)
        {
            string proposed;
            try { proposed = DeviceNameManager.ValidateName(newName.Text); }
            catch (ArgumentException ex) { status.Text = ex.Message; return; }
            var candidates = entries.Where(RegistryScanner.IsTrustedCandidate).ToList();
            if (candidates.Count == 0) { status.Text = "未找到可确认的改名候选。"; return; }
            if (!HasSafeCandidates() || candidates.Select(e => e.Data).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
            { status.Text = "设备名称状态异常，当前不能安全修改，请查看详细信息。"; return; }
            if (candidates.All(e => e.Data == proposed)) { status.Text = "新名称与当前名称相同，无需修改。"; return; }
            if (backup.State == BackupState.Corrupt) { status.Text = "原始配置备份损坏，本次修改已取消。"; return; }
            if (backup.State == BackupState.Missing && candidates.Any(e => e.Data != "OBS Virtual Camera"))
            { status.Text = "未找到原始配置备份，无法保证准确恢复；本次修改已取消。"; return; }
            if (MessageBox.Show(this, "将 " + candidates.Count + " 个名称值修改为“" + proposed + "”？",
                "确认修改", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            StartAction("rename", proposed);
        }

        void RestoreClicked(object sender, EventArgs args)
        {
            if (backup.State != BackupState.Valid) { status.Text = "未找到原始配置备份，无法保证准确恢复。"; return; }
            string original = backup.Backup.Entries.Select(e => e.OriginalValue).Distinct().Count() == 1 ?
                backup.Backup.Entries[0].OriginalValue : "备份中的逐项原始值";
            if (MessageBox.Show(this, "将把当前设备名称恢复为最初保存的名称：\n\n当前：" + name.Text +
                "\n恢复为：" + original + "\n\n是否继续？", "恢复原始名称",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            StartAction("restore", null);
        }

        void StartAction(string action, string requestedName)
        {
            if (action != "rename" && action != "restore") { status.Text = "无效操作。"; return; }
            if (action == "rename")
            { try { requestedName = DeviceNameManager.ValidateName(requestedName); }
              catch (ArgumentException ex) { status.Text = ex.Message; return; } }
            if (action == "restore" && backup.State != BackupState.Valid)
            { status.Text = "未找到有效的原始配置备份，无法恢复。"; return; }
            var candidates = entries.Where(RegistryScanner.IsTrustedCandidate).ToList();
            if (!HasSafeCandidates()) { status.Text = "未找到可确认的单个 OBS 设备。"; return; }
            if (action == "rename" && candidates.Select(e => e.Data).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
            { status.Text = "设备名称状态异常，当前不能安全修改。"; return; }
            if (candidates.Any(e => RegistryScanner.CheckWriteAccess(e) == 5) && !PrivilegeManager.IsAdministrator())
            {
                elevatedAction = action; elevatedName = requestedName;
                elevate.Visible = true; rename.Visible = restore.Visible = false;
                status.Text = "修改设备名称需要管理员权限。点击上方按钮继续。";
                return;
            }
            elevate.Visible = false; rename.Visible = restore.Visible = true;
            ExecuteAsync(action, requestedName);
        }

        void ElevateClicked(object sender, EventArgs args)
        {
            status.Text = "正在请求管理员权限...";
            string message;
            if (PrivilegeManager.LaunchElevated(elevatedAction, elevatedName, out message)) Close();
            else { status.Text = message; elevate.Visible = false; rename.Visible = restore.Visible = true; }
        }

        void ExecuteAsync(string action, string requestedName)
        {
            Busy(true); status.Text = "正在验证设备...";
            var scanSnapshot = new List<RegistryEntry>(entries);
            string oldName = Candidates().Select(e => e.Data).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1 ?
                Candidates()[0].Data : "当前名称";
            ThreadPool.QueueUserWorkItem(_ =>
            {
                RenameResult result;
                List<RegistryEntry> scanEntries = null;
                List<string> scanWarnings = null;
                BackupLoad load = null;
                try
                {
                    Action<string> progress = message => BeginInvoke((Action)(() => status.Text = message));
                    result = action == "rename" ? DeviceNameManager.Rename(scanSnapshot, requestedName, progress) :
                        DeviceNameManager.Restore(scanSnapshot, progress);
                    BeginInvoke((Action)(() => status.Text = "正在重新扫描名称..."));
                    scanEntries = RegistryScanner.Scan(out scanWarnings);
                    load = BackupManager.Load();
                    if (result.Success && action == "rename" && scanEntries.Where(RegistryScanner.IsTrustedCandidate).Any(e => e.Data != requestedName))
                    { result.Success = false; result.Message = "重新扫描的名称与目标不一致。"; }
                    if (result.Success && action == "restore" && load.State == BackupState.Valid &&
                        load.Backup.Entries.Any(item => !scanEntries.Any(e => RegistryScanner.IsTrustedCandidate(e) &&
                            e.View == item.ViewLabel && e.ValueName == item.DisplayValueName && e.Data == item.OriginalValue)))
                    { result.Success = false; result.Message = "恢复后重新扫描验证失败。"; }
                }
                catch (Exception ex)
                { result = new RenameResult { Message = "操作遇到异常：" + ex.Message }; Logger.Write(action + " failure " + ex.GetType().Name); }
                BeginInvoke((Action)(() => {
                    lastResult = result;
                    if (scanEntries != null) { entries = scanEntries; warnings = scanWarnings; backup = load; ShowScan(); }
                    if (result.Success)
                    {
                        string now = Candidates().Select(e => e.Data).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1 ?
                            Candidates()[0].Data : (action == "rename" ? requestedName : "原始名称");
                        if (action == "restore") newName.Clear();
                        status.Text = (action == "rename" ? "✓ 修改完成：" : "✓ 已恢复原始名称：") +
                            oldName + " → " + now + "。若其他软件仍显示旧名称，请重新打开。";
                    }
                    else if (!result.RollbackComplete)
                        status.Text = "⚠ 部分配置未能恢复，请查看详细信息或日志。";
                    else if (result.Failed > 0)
                        status.Text = "⚠ 操作未完成，成功 " + result.Succeeded + " 项，失败 " + result.Failed + " 项。" + result.Message;
                    else status.Text = result.Message;
                    Busy(false);
                }));
            });
        }

        void ShowDetails()
        {
            using (var form = new Form { Text = "技术信息", Size = new Size(1200, 560), StartPosition = FormStartPosition.CenterParent,
                                        Font = new Font("Segoe UI", 9), BackColor = Color.White })
            {
                var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true };
                list.Columns.Add("状态", 115); list.Columns.Add("视图", 65); list.Columns.Add("注册表路径", 475);
                list.Columns.Add("Value Name", 110); list.Columns.Add("当前值 / 旧值", 130);
                list.Columns.Add("可写", 70); list.Columns.Add("新值", 115); list.Columns.Add("错误", 230);
                if (lastResult == null)
                    foreach (var e in entries) AddRow(list, e.Decision, e.View, e.Path, e.ValueName, e.Data,
                        RegistryScanner.IsTrustedCandidate(e) ? WriteAccess(e) : "未检测", "", "");
                else
                    foreach (var r in lastResult.Entries)
                    {
                        var found = entries.FirstOrDefault(e => e.Path == r.RegistryPath);
                        AddRow(list, r.Status, found == null ? "" : found.View, r.RegistryPath, r.ValueName,
                            r.OldValue, found == null ? "未知" : WriteAccess(found), r.NewValue, r.ErrorMessage == null ? "" : r.ErrorMessage +
                            (r.ErrorCode == 0 ? "" : "（代码 " + r.ErrorCode + "）"));
                    }
                var footer = new TextBox { Dock = DockStyle.Bottom, Height = 50, Multiline = true, ReadOnly = true,
                    Text = warnings.Count == 0 ? "双击一行可复制。原始备份：" + BackupManager.PathName : string.Join(Environment.NewLine, warnings.ToArray()) };
                list.DoubleClick += (s, e) => { if (list.SelectedItems.Count > 0)
                    Clipboard.SetText(string.Join("\t", list.SelectedItems[0].SubItems.Cast<ListViewItem.ListViewSubItem>().Select(x => x.Text))); };
                form.Controls.Add(list); form.Controls.Add(footer);
                form.ShowDialog(this);
            }
        }

        static string WriteAccess(RegistryEntry entry)
        {
            int code = RegistryScanner.CheckWriteAccess(entry);
            return code == 0 ? "是" : code == 5 ? "否" : "错误 " + code;
        }

        void ShowMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("查看详细信息", null, (s, e) => ShowDetails());
            menu.Items.Add("打开日志", null, (s, e) => {
                string folder = Path.GetDirectoryName(Logger.PathName);
                if (!Directory.Exists(folder)) MessageBox.Show(this, "当前还没有日志文件。", Text);
                else Process.Start("explorer.exe", PrivilegeManager.Quote(folder));
            });
            menu.Items.Add("打开备份目录", null, (s, e) => {
                string folder = Path.GetDirectoryName(BackupManager.PathName);
                if (!Directory.Exists(folder)) MessageBox.Show(this, "当前还没有备份目录。", Text);
                else Process.Start("explorer.exe", PrivilegeManager.Quote(folder));
            });
#if DEBUG
            menu.Items.Add("诊断模式", null, (s, e) => ShowDiagnostics());
#endif
            menu.Items.Add("关于", null, (s, e) => ShowAbout());
            menu.Closed += (s, e) => menu.Dispose();
            menu.Show(menuButton, new Point(0, menuButton.Height));
        }

        void ShowAbout()
        {
            MessageBox.Show(this, "OBS Virtual Camera Name Manager\nVersion " + AppVersion.Text +
                "\n\n用于管理 Windows 中 OBS Virtual Camera 的显示名称。" +
                "\n\n支持自定义名称、一键恢复、原始配置备份和结果验证。" +
                "\n\n本工具不修改 OBS 核心文件，不安装驱动，不删除设备。" +
                "\n\n项目主页：" + AppVersion.ProjectUrl,
                "关于", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

#if DEBUG
        void ShowDiagnostics()
        {
            var lines = new List<string>();
            lines.Add("诊断模式 · Version " + AppVersion.Text);
            lines.Add("管理员：" + (PrivilegeManager.IsAdministrator() ? "是" : "否"));
            lines.Add("RegistryEntry 数量：" + entries.Count);
            lines.Add("识别依据：OBS Virtual Camera DirectShow CLSID " + RegistryScanner.CameraClsid);
            lines.Add("当前设备名称：" + name.Text);
            lines.Add("备份状态：" + backup.State);
            lines.Add("备份版本：" + (backup.Backup == null ? "无" : backup.Backup.Version.ToString()));
            lines.Add("备份哈希：" + (backup.State == BackupState.Valid && backup.Backup.Version == 2 ? "验证通过" :
                backup.State == BackupState.Valid ? "旧版 v1，无哈希字段" : "无有效备份"));
            lines.Add("最近一次操作：" + (lastResult == null ? "无" : lastResult.Message));
            foreach (var entry in entries)
                lines.Add(entry.View + " | " + entry.ValueName + " | " + entry.Data + " | 可写：" +
                    (RegistryScanner.IsTrustedCandidate(entry) ? WriteAccess(entry) : "未检测") + " | " + entry.Decision);
            using (var form = new Form { Text = "诊断模式", Size = new Size(760, 500), StartPosition = FormStartPosition.CenterParent })
            {
                form.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
                    ScrollBars = ScrollBars.Both, WordWrap = false, Text = string.Join(Environment.NewLine, lines.ToArray()) });
                form.ShowDialog(this);
            }
        }
#endif

        static void AddRow(ListView list, params string[] parts)
        {
            var row = new ListViewItem(parts[0]);
            foreach (var part in parts.Skip(1)) row.SubItems.Add(part ?? "");
            list.Items.Add(row);
        }
    }
}
