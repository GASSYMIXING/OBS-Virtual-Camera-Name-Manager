using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace OBSVCNameManager
{
    internal sealed class RegistryEntry
    {
        internal string Path, ValueName, Data, View, Decision;
        internal uint ValueType;
        internal RegistryEntry(string path, string valueName, string data, string view, string decision, uint valueType)
        { Path = path; ValueName = valueName; Data = data; View = view; Decision = decision; ValueType = valueType; }
    }

    internal static class RegistryScanner
    {
        internal const string CameraClsid = "{A3FCE0F5-3493-419F-958A-ABA1250EC20B}";
        const string VideoCategory = "{860BB310-5D01-11D0-BD3B-00A0C911CE86}";
        const uint KeyRead = 0x20019, KeySetValue = 0x0002, View64 = 0x100, View32 = 0x200;
        const int Success = 0, NoMoreItems = 259;
        static readonly IntPtr LocalMachine = new IntPtr(unchecked((int)0x80000002));

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegOpenKeyExW")]
        static extern int Open(IntPtr root, string path, uint options, uint access, out IntPtr key);
        [DllImport("advapi32.dll", EntryPoint = "RegCloseKey")]
        static extern int Close(IntPtr key);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegEnumKeyExW")]
        static extern int EnumKey(IntPtr key, uint index, StringBuilder name, ref uint length, IntPtr reserved,
            IntPtr className, IntPtr classLength, IntPtr lastWrite);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegEnumValueW")]
        static extern int EnumValue(IntPtr key, uint index, StringBuilder name, ref uint length, IntPtr reserved,
            out uint type, IntPtr data, IntPtr dataLength);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegQueryValueExW")]
        static extern int Query(IntPtr key, string name, IntPtr reserved, out uint type, byte[] data, ref uint length);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegSetValueExW")]
        static extern int Set(IntPtr key, string name, uint reserved, uint type, byte[] data, uint length);

        internal static bool IsTrustedCandidate(RegistryEntry entry)
        {
            if (entry.Decision != "改名候选" || (entry.View != "32 位" && entry.View != "64 位")) return false;
            string prefix = entry.View == "32 位" ? @"HKLM\SOFTWARE\WOW6432Node\Classes\CLSID\" : @"HKLM\SOFTWARE\Classes\CLSID\";
            return (entry.Path.Equals(prefix + CameraClsid, StringComparison.OrdinalIgnoreCase) && entry.ValueName == "(默认)") ||
                (entry.Path.Equals(prefix + VideoCategory + @"\Instance\" + CameraClsid, StringComparison.OrdinalIgnoreCase) && entry.ValueName == "FriendlyName");
        }

        static string LogicalPath(RegistryEntry entry)
        {
            string path = entry.Path.Substring(@"HKLM\".Length);
            return entry.View == "32 位" ? path.Replace(@"SOFTWARE\WOW6432Node\Classes\", @"SOFTWARE\Classes\") : path;
        }

        internal static bool TryRead(RegistryEntry entry, out string value, out uint type, out int error)
        {
            value = null; type = 0;
            IntPtr key;
            error = Open(LocalMachine, LogicalPath(entry), 0, KeyRead | (entry.View == "32 位" ? View32 : View64), out key);
            if (error != Success) return false;
            try { return ReadString(key, entry.ValueName == "(默认)" ? "" : entry.ValueName, out value, out type, out error); }
            finally { Close(key); }
        }

        internal static int Write(RegistryEntry entry, string value, uint type)
        {
            IntPtr key;
            int error = Open(LocalMachine, LogicalPath(entry), 0, KeySetValue | (entry.View == "32 位" ? View32 : View64), out key);
            if (error != Success) return error;
            try
            {
                byte[] bytes = Encoding.Unicode.GetBytes(value + "\0");
                return Set(key, entry.ValueName == "(默认)" ? "" : entry.ValueName, 0, type, bytes, (uint)bytes.Length);
            }
            finally { Close(key); }
        }

        internal static int CheckWriteAccess(RegistryEntry entry)
        {
            IntPtr key;
            int error = Open(LocalMachine, LogicalPath(entry), 0, KeySetValue | (entry.View == "32 位" ? View32 : View64), out key);
            if (error == Success) Close(key);
            return error;
        }

        internal static List<RegistryEntry> Scan(out List<string> warnings)
        {
            var result = new List<RegistryEntry>();
            warnings = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string clsid = @"SOFTWARE\Classes\CLSID\";
            foreach (var view in new[] { View64, View32 })
            {
                string label = view == View64 ? "64 位" : "32 位";
                Visit(clsid + CameraClsid, view, label, false, result, seen, warnings);
                Visit(clsid + VideoCategory + @"\Instance", view, label, true, result, seen, warnings);
            }
            foreach (var path in new[] { @"SYSTEM\CurrentControlSet\Enum", @"SYSTEM\CurrentControlSet\DeviceClasses",
                                           @"SYSTEM\CurrentControlSet\Control\Class" })
                Visit(path, 0, "系统", true, result, seen, warnings);
            result.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Path + a.ValueName, b.Path + b.ValueName));
            return result;
        }

        static void Visit(string path, uint view, string viewLabel, bool recursive, List<RegistryEntry> result,
                          HashSet<string> seen, List<string> warnings)
        {
            IntPtr key;
            int status = Open(LocalMachine, path, 0, KeyRead | view, out key);
            if (status == 2) return; // This registry branch is optional.
            if (status != Success) { warnings.Add(path + "：读取失败（" + status + "）"); return; }
            try
            {
                var names = new List<string>();
                for (uint i = 0; ; i++)
                {
                    var name = new StringBuilder(16384);
                    uint length = (uint)name.Capacity;
                    uint type;
                    status = EnumValue(key, i, name, ref length, IntPtr.Zero, out type, IntPtr.Zero, IntPtr.Zero);
                    if (status == NoMoreItems) break;
                    if (status != Success) { warnings.Add(path + "：枚举值失败（" + status + "）"); break; }
                    string valueName = name.ToString();
                    if (valueName.Length == 0 || valueName.Equals("FriendlyName", StringComparison.OrdinalIgnoreCase) ||
                        valueName.Equals("DeviceDesc", StringComparison.OrdinalIgnoreCase)) names.Add(valueName);
                }
                bool anchored = path.Equals(@"SOFTWARE\Classes\CLSID\" + CameraClsid, StringComparison.OrdinalIgnoreCase) ||
                    path.Equals(@"SOFTWARE\Classes\CLSID\" + VideoCategory + @"\Instance\" + CameraClsid, StringComparison.OrdinalIgnoreCase);
                foreach (string name in names)
                {
                    string data;
                    uint valueType;
                    int readError;
                    if (!ReadString(key, name, out data, out valueType, out readError)) continue;
                    if (!anchored && !data.Equals("OBS Virtual Camera", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!data.Equals("OBS Virtual Camera", StringComparison.OrdinalIgnoreCase) && !anchored) continue;
                    // A known OBS filter CLSID is a strong identity; same-name PnP records need review.
                    string decision = anchored && (name.Length == 0 || name.Equals("FriendlyName", StringComparison.OrdinalIgnoreCase))
                        ? "改名候选" : "关联信息／待核实";
                    string displayPath = view == View32 && path.StartsWith(@"SOFTWARE\Classes\", StringComparison.OrdinalIgnoreCase)
                        ? @"SOFTWARE\WOW6432Node\Classes\" + path.Substring(@"SOFTWARE\Classes\".Length) : path;
                    string full = @"HKLM\" + displayPath;
                    string identity = viewLabel + "|" + full + "|" + name;
                    if (seen.Add(identity)) result.Add(new RegistryEntry(full, name.Length == 0 ? "(默认)" : name, data, viewLabel, decision, valueType));
                }
                if (!recursive) return;
                for (uint i = 0; ; i++)
                {
                    var child = new StringBuilder(512);
                    uint length = (uint)child.Capacity;
                    status = EnumKey(key, i, child, ref length, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                    if (status == NoMoreItems) break;
                    if (status != Success) { warnings.Add(path + "：枚举子项失败（" + status + "）"); break; }
                    if (child.ToString().Equals("Properties", StringComparison.OrdinalIgnoreCase)) continue;
                    Visit(path + @"\" + child, view, viewLabel, true, result, seen, warnings);
                }
            }
            finally { Close(key); }
        }

        static bool ReadString(IntPtr key, string name, out string value, out uint type, out int error)
        {
            value = null;
            type = 0;
            uint length = 0;
            error = Query(key, name, IntPtr.Zero, out type, null, ref length);
            if (error != Success || (type != 1 && type != 2) || length > 65536 || length % 2 != 0) return false;
            var bytes = new byte[length];
            error = Query(key, name, IntPtr.Zero, out type, bytes, ref length);
            if (error != Success) return false;
            value = Encoding.Unicode.GetString(bytes, 0, (int)length).TrimEnd('\0');
            return true;
        }
    }
}
