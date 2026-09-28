using System.Reflection;

[assembly: AssemblyTitle("OBS Virtual Camera Name Manager")]
[assembly: AssemblyDescription("Manage the display name of OBS Virtual Camera on Windows")]
[assembly: AssemblyProduct("OBS Virtual Camera Name Manager")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyVersion(OBSVCNameManager.AppVersion.File)]
[assembly: AssemblyFileVersion(OBSVCNameManager.AppVersion.File)]

namespace OBSVCNameManager
{
    internal static class AppVersion
    {
        internal const string Text = "1.0.0";
        internal const string File = Text + ".0";
        internal const string ProjectUrl = "PROJECT_URL";
    }
}
