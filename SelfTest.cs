using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;

namespace OBSVCNameManager
{
    internal sealed class FakeStore : IRegistryStore
    {
        internal readonly Dictionary<string, RegistryState> Values = new Dictionary<string, RegistryState>();
        internal int WriteCalls, FailWriteCall, FailRollbackCall;
        static string Key(RegistryEntry e) { return e.Path + "|" + e.ValueName; }
        internal FakeStore(IEnumerable<RegistryEntry> entries)
        { foreach (var e in entries) Values[Key(e)] = new RegistryState { Ok = true, Value = e.Data, Type = e.ValueType }; }
        public RegistryState Read(RegistryEntry e)
        {
            RegistryState item;
            if (!Values.TryGetValue(Key(e), out item)) return new RegistryState { Error = 2 };
            return new RegistryState { Ok = true, Value = item.Value, Type = item.Type };
        }
        public int Write(RegistryEntry e, string value, uint type)
        {
            WriteCalls++;
            if (WriteCalls == FailWriteCall || WriteCalls == FailRollbackCall) return 5;
            Values[Key(e)] = new RegistryState { Ok = true, Value = value, Type = type };
            return 0;
        }
    }

    internal static class SelfTest
    {
        static void Check(bool condition, string message)
        { if (!condition) throw new Exception("自检失败：" + message); }

        internal static string Run()
        {
            Check(DeviceNameManager.ValidateName("  Studio Camera  ") == "Studio Camera", "去空格");
            foreach (string bad in new[] { "", "\nCamera", "Camera\0One", new string('a', 65) })
            {
                bool rejected = false;
                try { DeviceNameManager.ValidateName(bad); } catch (ArgumentException) { rejected = true; }
                Check(rejected, "输入检查");
            }
            Check(PrivilegeManager.Quote("GASBOX \"Camera\"") == "\"GASBOX \\\"Camera\\\"\"", "命令行引号");
            Check(PrivilegeManager.Quote("Studio Camera") == "\"Studio Camera\"", "命令行空格");
            bool runningElevated = PrivilegeManager.IsAdministrator();

            List<string> warnings;
            var entries = RegistryScanner.Scan(out warnings).Where(RegistryScanner.IsTrustedCandidate).ToList();
            Check(entries.Count > 0, "未识别 OBS 设备");
            var stale = entries.Select(e => new RegistryEntry(e.Path, e.ValueName, "旧扫描值", e.View, e.Decision, e.ValueType)).ToList();
            var staleResult = DeviceNameManager.TestTransaction(stale, "Camera", new FakeStore(entries));
            Check(!staleResult.Success, "过期扫描未阻断");

            string folder = Path.Combine(Path.GetTempPath(), "obsvc-phase3-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "backup.json");
            try
            {
                var backup = BackupManager.CreateOrValidate(entries, path);
                Check(backup.Version == 2 && backup.EntryCount == entries.Count && backup.Entries.All(e => e.RegistryView == "32" || e.RegistryView == "64"), "v2 备份格式");
                byte[] original = File.ReadAllBytes(path);
                string editedJson = System.Text.Encoding.UTF8.GetString(original).Replace("OBS Virtual Camera", "USB Virtual Camera");
                File.WriteAllText(path, editedJson);
                Check(BackupManager.Load(path, false).State == BackupState.Corrupt, "v2 哈希校验");
                File.WriteAllBytes(path, original);
                var renamed = entries.Select(e => new RegistryEntry(e.Path, e.ValueName, "Studio Camera", e.View, e.Decision, e.ValueType)).ToList();
                BackupManager.CreateOrValidate(renamed, path);
                BackupManager.CreateOrValidate(entries, path); // Rename after a simulated restore.
                Check(original.SequenceEqual(File.ReadAllBytes(path)), "原始备份被覆盖");
                File.WriteAllText(path, "{bad json");
                var corrupt = BackupManager.Load(path, true);
                Check(corrupt.State == BackupState.Corrupt && !File.Exists(path) &&
                    Directory.GetFiles(folder, "backup.corrupted.*.json").Length == 1, "损坏备份保留");

                var v1 = new BackupFile { Version = 1, CreatedAt = DateTimeOffset.Now.ToString("o"),
                    Entries = entries.Select(e => new BackupEntry { Path = e.Path,
                        ValueName = e.ValueName == "(默认)" ? "" : e.ValueName,
                        OriginalValue = e.Data, ValueType = e.ValueType == 1 ? "REG_SZ" : "REG_EXPAND_SZ" }).ToList() };
                using (var stream = File.Create(path)) new DataContractJsonSerializer(typeof(BackupFile)).WriteObject(stream, v1);
                Check(BackupManager.Load(path, false).State == BackupState.Valid, "v1 备份兼容");
                File.Delete(path);
                Check(BackupManager.Load(path, false).State == BackupState.Missing, "丢失备份状态");

                var fake = new FakeStore(entries) { FailWriteCall = 3 };
                var failed = DeviceNameManager.TestTransaction(entries, "Studio Camera", fake);
                Check(!failed.Success && failed.RollbackComplete && entries.All(e => fake.Read(e).Value == e.Data), "第三项失败回滚");
                fake = new FakeStore(entries) { FailWriteCall = 3, FailRollbackCall = 4 };
                failed = DeviceNameManager.TestTransaction(entries, "Studio Camera", fake);
                Check(!failed.Success && !failed.RollbackComplete && failed.Message.Contains("回滚不完整"), "回滚失败提示");
                fake = new FakeStore(renamed);
                var restored = DeviceNameManager.TestTransaction(renamed, "OBS Virtual Camera", fake);
                Check(restored.Success && entries.All(e => fake.Read(e).Value == "OBS Virtual Camera"), "恢复事务验证");
                return "PASS: 输入、参数转义、设备身份、v2 原子备份、v1 兼容、备份保留、丢失状态、写入失败回滚、回滚失败提示、恢复事务；真实注册表只读。管理员令牌=" + runningElevated;
            }
            finally
            {
                foreach (string file in Directory.GetFiles(folder)) File.Delete(file);
                Directory.Delete(folder, false);
            }
        }
    }
}
