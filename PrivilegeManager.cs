using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace OBSVCNameManager
{
    internal static class PrivilegeManager
    {
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool GetTokenInformation(IntPtr token, int informationClass, out int elevation, int length, out int returned);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct ShellExecuteInfo
        {
            public uint cbSize, fMask;
            public IntPtr hwnd;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpVerb, lpFile, lpParameters, lpDirectory;
            public int nShow;
            public IntPtr hInstApp, lpIDList;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpClass;
            public IntPtr hkeyClass;
            public uint dwHotKey;
            public IntPtr hIcon, hProcess;
        }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "ShellExecuteExW", SetLastError = true)]
        static extern bool ShellExecuteEx(ref ShellExecuteInfo info);

        internal static bool IsAdministrator()
        {
            IntPtr token;
            if (!OpenProcessToken(GetCurrentProcess(), 0x0008, out token)) return false;
            try
            {
                int elevated, returned;
                return GetTokenInformation(token, 20, out elevated, sizeof(int), out returned) && elevated != 0;
            }
            finally { CloseHandle(token); }
        }

        internal static string Quote(string argument)
        {
            var output = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in argument)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"')
                {
                    output.Append('\\', slashes * 2 + 1).Append('"');
                    slashes = 0;
                    continue;
                }
                output.Append('\\', slashes).Append(c);
                slashes = 0;
            }
            output.Append('\\', slashes * 2).Append('"');
            return output.ToString();
        }

        internal static bool LaunchElevated(string action, string name, out string message)
        {
            string parameters = "--elevated --action " + action;
            if (action == "rename") parameters += " --name " + Quote(name);
            parameters += " --backup-path " + Quote(BackupManager.PathName);
            var info = new ShellExecuteInfo {
                cbSize = (uint)Marshal.SizeOf(typeof(ShellExecuteInfo)), lpVerb = "runas",
                lpFile = Application.ExecutablePath, lpParameters = parameters,
                lpDirectory = System.IO.Path.GetDirectoryName(Application.ExecutablePath), nShow = 1
            };
            if (ShellExecuteEx(ref info)) { message = "已启动管理员进程。"; return true; }
            int error = Marshal.GetLastWin32Error();
            message = error == 1223 ? "已取消管理员授权，未进行任何修改。" :
                "无法启动管理员进程：" + new Win32Exception(error).Message + "（代码 " + error + "）";
            Logger.Write("elevation failed code=" + error);
            return false;
        }
    }
}
