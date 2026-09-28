using System;
using System.IO;

namespace OBSVCNameManager
{
    internal static class Logger
    {
        internal static string PathName { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OBSVCNameManager", "logs", "app.log"); } }
        internal static void Write(string eventText)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PathName));
                File.AppendAllText(PathName, DateTimeOffset.Now.ToString("o") + " " + eventText + Environment.NewLine);
            }
            catch { /* Logging failure must not interrupt a registry operation. */ }
        }
    }
}
