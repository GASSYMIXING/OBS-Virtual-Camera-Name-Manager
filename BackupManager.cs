using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;

namespace OBSVCNameManager
{
    [DataContract]
    internal sealed class BackupFile
    {
        [DataMember(Name = "version")] public int Version;
        [DataMember(Name = "createdAt")] public string CreatedAt;
        [DataMember(Name = "applicationVersion", EmitDefaultValue = false)] public string ApplicationVersion;
        [DataMember(Name = "windowsVersion", EmitDefaultValue = false)] public string WindowsVersion;
        [DataMember(Name = "entryCount", EmitDefaultValue = false)] public int EntryCount;
        [DataMember(Name = "backupHash", EmitDefaultValue = false)] public string BackupHash;
        [DataMember(Name = "entries")] public List<BackupEntry> Entries;
    }

    [DataContract]
    internal sealed class BackupEntry
    {
        [DataMember(Name = "path")] public string Path;
        [DataMember(Name = "valueName")] public string ValueName;
        [DataMember(Name = "originalValue")] public string OriginalValue;
        [DataMember(Name = "valueType")] public string ValueType;
        [DataMember(Name = "registryView", EmitDefaultValue = false)] public string RegistryView;

        internal string ViewLabel { get { return RegistryView == "32" ? "32 位" : "64 位"; } }
        internal uint TypeCode { get { return ValueType == "REG_SZ" ? 1u : 2u; } }
        internal string DisplayValueName { get { return ValueName == "" ? "(默认)" : ValueName; } }
        internal RegistryEntry AsRegistryEntry()
        { return new RegistryEntry(Path, DisplayValueName, OriginalValue, ViewLabel, "改名候选", TypeCode); }
    }

    internal enum BackupState { Missing, Valid, Corrupt }
    internal sealed class BackupLoad
    {
        internal BackupState State;
        internal BackupFile Backup;
        internal string Message;
    }

    internal static class BackupManager
    {
        static string pathOverride;
        internal static string PathName { get { return pathOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OBSVCNameManager", "backup.json"); } }

        internal static void UsePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || path.StartsWith(@"\\"))
                throw new ArgumentException("备份路径无效。");
            string full = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(full);
            if (!Path.GetFileName(full).Equals("backup.json", StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(directory).Equals("OBSVCNameManager", StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(Path.GetDirectoryName(directory)).Equals("Local", StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(directory))).Equals("AppData", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("备份路径无效。");
            pathOverride = full;
        }

        internal static BackupLoad Load(string overridePath = null, bool quarantine = true)
        {
            string path = overridePath ?? PathName;
            if (!File.Exists(path)) return new BackupLoad { State = BackupState.Missing, Message = "未找到原始配置备份，无法保证准确恢复。" };
            try { return new BackupLoad { State = BackupState.Valid, Backup = ReadValidated(path) }; }
            catch (UnauthorizedAccessException)
            { return new BackupLoad { State = BackupState.Corrupt, Message = "无法读取原始配置备份，恢复已禁用；原文件仍在原位置。" }; }
            catch (IOException)
            { return new BackupLoad { State = BackupState.Corrupt, Message = "原始配置备份暂时无法读取，恢复已禁用；原文件仍在原位置。" }; }
            catch (Exception ex)
            {
                string message = "检测到备份文件损坏，原文件已保留。";
                if (quarantine)
                {
                    try
                    {
                        string moved = Path.Combine(Path.GetDirectoryName(path), "backup.corrupted." + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "." + Guid.NewGuid().ToString("N") + ".json");
                        File.Move(path, moved);
                    }
                    catch { message = "检测到备份文件损坏；无法重命名，原文件仍在原位置。"; }
                }
                Logger.Write("backup corrupt " + ex.GetType().Name);
                return new BackupLoad { State = BackupState.Corrupt, Message = message };
            }
        }

        internal static BackupFile CreateOrValidate(List<RegistryEntry> entries, string overridePath = null)
        {
            string path = overridePath ?? PathName;
            var existing = Load(path, false);
            if (existing.State == BackupState.Corrupt)
                throw new InvalidDataException("原始配置备份损坏，为避免无法恢复，本次修改已取消。");
            if (existing.State == BackupState.Valid)
            {
                if (!MatchesCurrent(existing.Backup, entries))
                    throw new InvalidDataException("原始配置备份与当前设备不匹配，本次修改已取消。");
                return existing.Backup;
            }
            if (entries.Count == 0 || entries.Any(e => !RegistryScanner.IsTrustedCandidate(e) || e.Data != "OBS Virtual Camera" || (e.ValueType != 1 && e.ValueType != 2)))
                throw new InvalidDataException("没有可信的原始名称，无法创建原始配置备份。");
            var backup = new BackupFile {
                Version = 2, CreatedAt = DateTimeOffset.Now.ToString("o"), ApplicationVersion = AppVersion.Text,
                WindowsVersion = Environment.OSVersion.VersionString, EntryCount = entries.Count,
                Entries = entries.Select(e => new BackupEntry {
                    Path = e.Path, ValueName = e.ValueName == "(默认)" ? "" : e.ValueName,
                    OriginalValue = e.Data, ValueType = e.ValueType == 1 ? "REG_SZ" : "REG_EXPAND_SZ",
                    RegistryView = e.View == "32 位" ? "32" : "64"
                }).ToList()
            };
            backup.BackupHash = Hash(backup);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { new DataContractJsonSerializer(typeof(BackupFile)).WriteObject(stream, backup); stream.Flush(true); }
                ReadValidated(temp); // Verify the flushed temporary file before publishing it.
                File.Move(temp, path); // Same-directory rename; never replaces an existing backup.
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            return ReadValidated(path);
        }

        internal static bool MatchesCurrent(BackupFile backup, List<RegistryEntry> entries)
        {
            if (backup.Entries.Count != entries.Count) return false;
            foreach (var item in backup.Entries)
                if (!entries.Any(e => RegistryScanner.IsTrustedCandidate(e) && e.View == item.ViewLabel &&
                    e.ValueName == item.DisplayValueName && e.ValueType == item.TypeCode)) return false;
            return true;
        }

        static BackupFile ReadValidated(string path)
        {
            BackupFile backup;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                backup = (BackupFile)new DataContractJsonSerializer(typeof(BackupFile)).ReadObject(stream);
            DateTimeOffset created;
            if (backup == null || (backup.Version != 1 && backup.Version != 2) ||
                !DateTimeOffset.TryParse(backup.CreatedAt, out created) || backup.Entries == null || backup.Entries.Count == 0 || backup.Entries.Count > 64)
                throw new InvalidDataException("备份结构无效");
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in backup.Entries)
            {
                if (item == null || item.Path == null || item.ValueName == null || item.OriginalValue == null ||
                    (item.ValueType != "REG_SZ" && item.ValueType != "REG_EXPAND_SZ"))
                    throw new InvalidDataException("备份项目无效");
                if (backup.Version == 1)
                {
                    item.RegistryView = item.Path.IndexOf(@"\WOW6432Node\", StringComparison.OrdinalIgnoreCase) >= 0 ? "32" : "64";
                    if (item.OriginalValue != "OBS Virtual Camera") throw new InvalidDataException("旧版原始值无效");
                }
                if ((item.RegistryView != "32" && item.RegistryView != "64") ||
                    !RegistryScanner.IsTrustedCandidate(item.AsRegistryEntry()) ||
                    !ids.Add(item.RegistryView + "|" + item.Path + "|" + item.ValueName))
                    throw new InvalidDataException("备份身份或项目重复");
            }
            if (backup.Version == 2 && (backup.EntryCount != backup.Entries.Count ||
                string.IsNullOrWhiteSpace(backup.ApplicationVersion) || string.IsNullOrWhiteSpace(backup.WindowsVersion) ||
                !string.Equals(backup.BackupHash, Hash(backup), StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("备份完整性检查失败");
            return backup;
        }

        static string Hash(BackupFile backup)
        {
            var text = new StringBuilder();
            Action<string> add = s => { s = s ?? ""; text.Append(s.Length).Append(':').Append(s); };
            add(backup.Version.ToString()); add(backup.CreatedAt); add(backup.ApplicationVersion);
            add(backup.WindowsVersion); add(backup.EntryCount.ToString());
            foreach (var e in backup.Entries)
            { add(e.Path); add(e.ValueName); add(e.OriginalValue); add(e.ValueType); add(e.RegistryView); }
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "");
        }
    }
}
