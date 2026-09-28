#if DEBUG
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace OBSVCNameManager
{
    // Explicit development-only integration test. Never included in a Release build.
    internal static class LiveTest
    {
        static List<RegistryEntry> Scan(string expected)
        {
            List<string> warnings;
            var candidates = RegistryScanner.Scan(out warnings).Where(RegistryScanner.IsTrustedCandidate).ToList();
            if (warnings.Count != 0 || candidates.Count != 4 || candidates.Any(e => e.Data != expected))
                throw new InvalidOperationException("实机读回验证失败：" + expected + "，候选项 " + candidates.Count + "，警告 " + warnings.Count);
            return candidates;
        }

        static void Change(string name)
        {
            var result = DeviceNameManager.Rename(ScanCurrent(), name, text => { });
            if (!result.Success || result.Total != 4 || result.Succeeded != 4 || result.Entries.Any(e => !e.Success))
                throw new InvalidOperationException("修改到 " + name + " 失败：" + result.Message);
            Scan(name);
        }

        static List<RegistryEntry> ScanCurrent()
        {
            List<string> warnings;
            var candidates = RegistryScanner.Scan(out warnings).Where(RegistryScanner.IsTrustedCandidate).ToList();
            if (warnings.Count != 0 || candidates.Count != 4 ||
                candidates.Select(e => e.Data).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
                throw new InvalidOperationException("设备状态不适合实机测试。");
            return candidates;
        }

        static void Restore()
        {
            var result = DeviceNameManager.Restore(ScanCurrent(), text => { });
            if (!result.Success || result.Total != 4 || result.Succeeded != 4 || result.Entries.Any(e => !e.Success))
                throw new InvalidOperationException("恢复失败：" + result.Message);
            Scan("OBS Virtual Camera");
        }

        static void ProbeFreshProcess(string expected)
        {
            string output = Path.Combine(Path.GetTempPath(), "obsvc-probe-" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                var start = new ProcessStartInfo(Application.ExecutablePath, "--live-probe-output " + PrivilegeManager.Quote(output));
                start.UseShellExecute = false;
                start.CreateNoWindow = true;
                using (var process = Process.Start(start))
                {
                    if (!process.WaitForExit(30000) || process.ExitCode != 0) throw new InvalidOperationException("重新启动后的扫描失败。");
                }
                var names = File.ReadAllLines(output, Encoding.UTF8);
                if (names.Length != 4 || names.Any(line => !line.EndsWith("|" + expected, StringComparison.Ordinal)))
                    throw new InvalidOperationException("重新启动后的设备名称不匹配。");
            }
            finally { if (File.Exists(output)) File.Delete(output); }
        }

        internal static string Run()
        {
            if (!PrivilegeManager.IsAdministrator()) return "SKIP: 当前不是管理员进程；真实注册表未写入。";
            var report = new List<string>();
            bool changed = false;
            try
            {
                Scan("OBS Virtual Camera");
                var initial = BackupManager.Load(null, false);
                if (initial.State == BackupState.Corrupt ||
                    (initial.State == BackupState.Valid && initial.Backup.Entries.Any(e => e.OriginalValue != "OBS Virtual Camera")))
                    return "SKIP: 现有备份状态不适合实机测试；真实注册表未写入。";
                changed = true; Change("GASBOX Camera");
                var backup = BackupManager.Load(null, false);
                if (backup.State != BackupState.Valid || backup.Backup.Entries.Count != 4 ||
                    backup.Backup.Entries.Any(e => e.OriginalValue != "OBS Virtual Camera"))
                    throw new InvalidOperationException("首次备份不完整。");
                byte[] originalBackup = File.ReadAllBytes(BackupManager.PathName);
                ProbeFreshProcess("GASBOX Camera");
                report.Add("TEST 1 PASS: 四项写入、读回、备份、重新启动扫描。");
                Change("Studio Camera");
                if (!originalBackup.SequenceEqual(File.ReadAllBytes(BackupManager.PathName)))
                    throw new InvalidOperationException("第二次修改覆盖了原始备份。");
                report.Add("TEST 2 PASS: 后续修改没有覆盖备份。");
                Restore(); changed = false;
                report.Add("TEST 3 PASS: 四项恢复并读回验证。");
                ProbeFreshProcess("OBS Virtual Camera");
                changed = true; Change("Camera 01");
                if (!originalBackup.SequenceEqual(File.ReadAllBytes(BackupManager.PathName)))
                    throw new InvalidOperationException("再次修改覆盖了原始备份。");
                report.Add("TEST 4 PASS: 重新启动后再次修改，旧备份仍有效。");
                Restore(); changed = false;
                report.Add("FINAL PASS: 已恢复 OBS Virtual Camera，原始备份保留。");
            }
            catch (Exception ex)
            {
                report.Add("FAIL: " + ex.Message);
                if (changed)
                {
                    try { Restore(); report.Add("CLEANUP PASS: 已恢复原始名称。"); }
                    catch (Exception cleanup) { report.Add("CLEANUP FAIL: " + cleanup.Message); }
                }
            }
            return string.Join(Environment.NewLine, report.ToArray());
        }
    }
}
#endif
