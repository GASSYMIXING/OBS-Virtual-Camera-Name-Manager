using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;

namespace OBSVCNameManager
{
    internal sealed class RegistryState
    {
        internal bool Ok;
        internal string Value;
        internal uint Type;
        internal int Error;
    }
    internal interface IRegistryStore
    {
        RegistryState Read(RegistryEntry entry);
        int Write(RegistryEntry entry, string value, uint type);
    }
    internal sealed class LiveRegistryStore : IRegistryStore
    {
        public RegistryState Read(RegistryEntry entry)
        {
            string value; uint type; int error;
            bool ok = RegistryScanner.TryRead(entry, out value, out type, out error);
            return new RegistryState { Ok = ok, Value = value, Type = type, Error = !ok && error == 0 ? 13 : error };
        }
        public int Write(RegistryEntry entry, string value, uint type)
        { return RegistryScanner.Write(entry, value, type); }
    }
    internal sealed class RegistrySnapshotEntry
    {
        internal RegistryEntry Entry;
        internal string Value;
        internal uint Type;
        internal int RowIndex;
    }
    internal sealed class RenameEntryResult
    {
        internal string RegistryPath, ValueName, OldValue, NewValue, Status, ErrorMessage;
        internal int ErrorCode;
        internal bool Success;
    }
    internal sealed class RenameResult
    {
        internal bool Success, RollbackComplete = true;
        internal int Total, Succeeded, Failed;
        internal string Message;
        internal List<RenameEntryResult> Entries = new List<RenameEntryResult>();
    }

    internal static class DeviceNameManager
    {
        internal static string ValidateName(string input)
        {
            if ((input ?? "").Any(char.IsControl)) throw new ArgumentException("设备名称不能包含换行或控制字符。");
            string value = (input ?? "").Trim();
            if (value.Length == 0) throw new ArgumentException("请输入新的设备名称。");
            if (value.Length > 64) throw new ArgumentException("设备名称不能超过 64 个字符。");
            return value;
        }

        static RenameResult WithLock(Func<RenameResult> operation)
        {
            using (var gate = new Mutex(false, @"Local\OBSVCNameManagerOperations"))
            {
                bool acquired = false;
                try
                {
                    try { acquired = gate.WaitOne(30000); }
                    catch (AbandonedMutexException) { acquired = true; }
                    return acquired ? operation() : new RenameResult { Message = "另一项设备名称操作仍在进行，请稍后重试。" };
                }
                finally { if (acquired) gate.ReleaseMutex(); }
            }
        }

        internal static RenameResult Rename(List<RegistryEntry> scan, string input, Action<string> progress)
        { return WithLock(() => RenameCore(scan, input, progress)); }

        static RenameResult RenameCore(List<RegistryEntry> scan, string input, Action<string> progress)
        {
            string name = ValidateName(input);
            Logger.Write("rename begin");
            var targets = scan.Where(e => e.Decision == "改名候选").ToList();
            var result = NewResult(targets, name);
            if (targets.Count == 0) { result.Message = "未找到可确认的改名候选。"; return result; }
            if (targets.Count > 4 || targets.Any(e => !RegistryScanner.IsTrustedCandidate(e)) ||
                targets.Select(e => e.View + "|" + e.Path + "|" + e.ValueName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != targets.Count)
            { result.Message = "检测到多个或无法区分的候选设备，当前版本暂不支持自动修改。"; return result; }
            if (targets.Select(e => e.Data).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
            { result.Message = "设备名称状态异常：相关配置项名称不一致，请查看详细信息。"; return result; }
            var store = new LiveRegistryStore();
            progress("正在验证设备...");
            var snapshot = Prepare(targets, result, store, true, false);
            if (snapshot.Count != targets.Count)
            { result.Message = "设备二次验证失败，本次未修改注册表。"; Logger.Write("rename failure preflight code=" + result.Entries.First(e => e.Status == "失败").ErrorCode); return result; }
            if (snapshot.All(s => s.Value == name)) { result.Message = "新名称与当前名称相同，无需修改。"; return result; }
            try
            {
                progress("正在创建原始配置备份...");
                BackupManager.CreateOrValidate(snapshot.Select(s => new RegistryEntry(s.Entry.Path, s.Entry.ValueName,
                    s.Value, s.Entry.View, s.Entry.Decision, s.Type)).ToList());
            }
            catch (Exception ex)
            {
                Mark(result.Entries[0], 0, "备份失败", ex.Message);
                result.Failed++;
                result.Message = ex.Message;
                Logger.Write("rename failure backup " + ex.GetType().Name);
                return result;
            }
            Apply(snapshot, Enumerable.Repeat(name, snapshot.Count).ToList(), result, store, progress, "rename");
            return result;
        }

        internal static RenameResult Restore(List<RegistryEntry> scan, Action<string> progress)
        { return WithLock(() => RestoreCore(scan, progress)); }

        static RenameResult RestoreCore(List<RegistryEntry> scan, Action<string> progress)
        {
            Logger.Write("restore begin");
            var load = BackupManager.Load();
            if (load.State != BackupState.Valid)
            { Logger.Write("restore failure backup state=" + load.State); return new RenameResult { Message = load.Message }; }
            var result = new RenameResult { Total = load.Backup.Entries.Count };
            var targets = new List<RegistryEntry>();
            var goals = new List<string>();
            var targetRows = new List<int>();
            foreach (var item in load.Backup.Entries)
            {
                var row = new RenameEntryResult { RegistryPath = item.Path, ValueName = item.DisplayValueName,
                    NewValue = item.OriginalValue, Status = "未执行" };
                int rowIndex = result.Entries.Count;
                result.Entries.Add(row);
                var matches = scan.Where(e => RegistryScanner.IsTrustedCandidate(e) && e.View == item.ViewLabel &&
                    e.ValueName == item.DisplayValueName).ToList();
                if (matches.Count != 1)
                {
                    Mark(row, 2, "跳过", "原注册表位置已变化，无法安全恢复该项目。");
                    result.Failed++;
                    continue;
                }
                var target = matches[0];
                if (target.ValueType != item.TypeCode)
                {
                    Mark(row, 13, "跳过", "当前注册表值类型与原始备份不兼容。");
                    result.Failed++;
                    continue;
                }
                row.RegistryPath = target.Path; // Stable CLSID/view/value identity resolves a changed path.
                targets.Add(target); goals.Add(item.OriginalValue); targetRows.Add(rowIndex);
            }
            if (targets.Count == 0) { result.Message = "没有可安全恢复的注册表项目。"; Logger.Write("restore failure no targets"); return result; }
            progress("正在验证设备...");
            var store = new LiveRegistryStore();
            var snapshot = Prepare(targets, result, store, false, true, targetRows);
            if (snapshot.Count == 0) { result.Message = "没有可安全恢复的注册表项目。"; Logger.Write("restore failure preflight"); return result; }
            var readyGoals = snapshot.Select(s => goals[targetRows.IndexOf(s.RowIndex)]).ToList();
            Apply(snapshot, readyGoals, result, store, progress, "restore");
            return result;
        }

        static RenameResult NewResult(List<RegistryEntry> targets, string newName)
        {
            var result = new RenameResult { Total = targets.Count };
            foreach (var e in targets) result.Entries.Add(new RenameEntryResult {
                RegistryPath = e.Path, ValueName = e.ValueName, OldValue = e.Data,
                NewValue = newName, Status = "未执行"
            });
            return result;
        }

        static List<RegistrySnapshotEntry> Prepare(List<RegistryEntry> targets, RenameResult result, IRegistryStore store,
            bool matchScan, bool allowSkip, List<int> rowIndices = null)
        {
            var snapshot = new List<RegistrySnapshotEntry>();
            for (int i = 0; i < targets.Count; i++)
            {
                var entry = targets[i];
                int rowIndex = rowIndices == null ? i : rowIndices[i];
                RegistryState state = store.Read(entry);
                if (!RegistryScanner.IsTrustedCandidate(entry) || !state.Ok || state.Type != entry.ValueType ||
                    (matchScan && state.Value != entry.Data))
                {
                    Mark(result.Entries[rowIndex], state.Error, allowSkip ? "跳过" : "失败",
                        "注册表值不存在、类型不符，或扫描后发生变化。");
                    result.Failed++;
                    if (!allowSkip) return snapshot;
                    continue;
                }
                result.Entries[rowIndex].OldValue = state.Value;
                snapshot.Add(new RegistrySnapshotEntry { Entry = entry, Value = state.Value, Type = state.Type, RowIndex = rowIndex });
            }
            return snapshot;
        }

        static void Apply(List<RegistrySnapshotEntry> snapshot, List<string> goals, RenameResult result,
            IRegistryStore store, Action<string> progress, string action)
        {
            var changed = new List<int>();
            for (int i = 0; i < snapshot.Count; i++)
            {
                var item = snapshot[i];
                var row = result.Entries[item.RowIndex];
                try
                {
                    progress(action == "rename" ? "正在修改 " + (i + 1) + "/" + snapshot.Count + "..." :
                        "正在恢复 " + (i + 1) + "/" + snapshot.Count + "...");
                    var now = store.Read(item.Entry);
                    if (!now.Ok || now.Type != item.Type || now.Value != item.Value)
                    { Mark(row, now.Error, "失败", "操作前注册表值发生变化。"); result.Failed++; Logger.Write(action + " failure prewrite code=" + now.Error); Rollback(snapshot, changed, result, store, progress); return; }
                    int error = store.Write(item.Entry, goals[i], item.Type);
                    if (error != 0)
                    { Mark(row, error, "失败", FriendlyError(error)); result.Failed++; Logger.Write(action + " failure write code=" + error); Rollback(snapshot, changed, result, store, progress); return; }
                    changed.Add(i);
                    var check = store.Read(item.Entry);
                    if (!check.Ok || check.Value != goals[i] || check.Type != item.Type)
                    { Mark(row, check.Error, "失败", "写入后读取验证失败。"); result.Failed++; Logger.Write(action + " failure verify code=" + check.Error); Rollback(snapshot, changed, result, store, progress); return; }
                    row.Status = "成功"; row.Success = true;
                    result.Succeeded++;
                }
                catch (Exception ex)
                {
                    Mark(row, 0, "失败", "操作异常：" + ex.Message);
                    result.Failed++;
                    Logger.Write(action + " failure exception=" + ex.GetType().Name);
                    if (!changed.Contains(i)) changed.Add(i); // Unknown write state: attempt verified rollback.
                    Rollback(snapshot, changed, result, store, progress);
                    return;
                }
            }
            progress("正在验证修改结果...");
            result.Success = result.Failed == 0;
            result.Message = action == "rename"
                ? "修改完成。成功：" + result.Succeeded + "，失败：" + result.Failed + "。"
                : "恢复完成。成功：" + result.Succeeded + "，失败：" + result.Failed + "。";
            Logger.Write(action + (result.Success ? " success" : " partial") + " successCount=" + result.Succeeded + " failedCount=" + result.Failed);
        }

        static void Rollback(List<RegistrySnapshotEntry> snapshot, List<int> changed, RenameResult result,
            IRegistryStore store, Action<string> progress)
        {
            if (changed.Count == 0)
            { result.Message = result.Entries.Any(e => e.ErrorCode == 5) ? FriendlyError(5) : "操作失败，未修改注册表。请查看详细信息。"; return; }
            progress("正在回滚本次修改...");
            Logger.Write("rollback begin count=" + changed.Count);
            for (int j = changed.Count - 1; j >= 0; j--)
            {
                int i = changed[j];
                var item = snapshot[i];
                var row = result.Entries[item.RowIndex];
                try
                {
                    int error = store.Write(item.Entry, item.Value, item.Type);
                    var check = store.Read(item.Entry);
                    if (error == 0 && check.Ok && check.Value == item.Value && check.Type == item.Type)
                    {
                        row.Status = row.Status == "失败" ? "失败（已回滚）" : "已回滚";
                        row.Success = false;
                        result.Succeeded = Math.Max(0, result.Succeeded - 1);
                    }
                    else
                    {
                        Mark(row, error == 0 ? check.Error : error, "回滚失败", "部分注册表项目未能恢复，请查看详细信息。");
                        result.RollbackComplete = false;
                    }
                }
                catch (Exception ex)
                { Mark(row, 0, "回滚失败", ex.Message); result.RollbackComplete = false; }
            }
            result.Message = result.RollbackComplete ? "操作失败，回滚成功。" : "回滚不完整：部分注册表项目未能恢复，请查看详细信息。";
            Logger.Write("rollback " + (result.RollbackComplete ? "success" : "failure"));
        }

        static void Mark(RenameEntryResult row, int error, string status, string message)
        { row.Status = status; row.ErrorCode = error; row.ErrorMessage = message; row.Success = false; }

        internal static string FriendlyError(int code)
        {
            if (code == 5) return "Windows 拒绝了注册表写入请求，需要管理员权限。";
            if (code == 2) return "目标配置项已经不存在，可能是 OBS 或系统更新导致。";
            if (code == 13) return "读取到的配置格式异常，本次操作已取消。";
            return "Windows 无法完成操作：" + new Win32Exception(code).Message;
        }

        // Self-test hook: all reads and writes below use a simulated store, never the real registry.
        internal static RenameResult TestTransaction(List<RegistryEntry> entries, string goal, IRegistryStore store)
        {
            var result = NewResult(entries, goal);
            var snapshot = Prepare(entries, result, store, true, false);
            if (snapshot.Count == entries.Count)
                Apply(snapshot, Enumerable.Repeat(goal, snapshot.Count).ToList(), result, store, text => { }, "rename");
            return result;
        }
    }
}
