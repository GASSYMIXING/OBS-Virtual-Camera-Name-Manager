param([ValidateSet('Release', 'Debug')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$output = Join-Path $here "build\$Configuration"
New-Item -ItemType Directory -Force -Path $output | Out-Null
$sources = @('AppVersion.cs', 'Main.cs', 'RegistryScanner.cs', 'BackupManager.cs',
    'DeviceNameManager.cs', 'PrivilegeManager.cs', 'Logger.cs', 'SelfTest.cs', 'LiveTest.cs') |
    ForEach-Object { Join-Path $here $_ }
$flags = if ($Configuration -eq 'Debug') { @('/debug:full', '/optimize-', '/define:DEBUG') }
    else { @('/debug:pdbonly', '/optimize+') }
$arguments = @('/nologo', '/target:winexe', '/platform:x64',
    "/out:$(Join-Path $output 'OBSVCNameManager.exe')",
    "/win32icon:$(Join-Path $here 'resources\app.ico')",
    '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll',
    '/reference:System.Runtime.Serialization.dll') + $flags + $sources
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw '编译失败' }
Write-Output (Join-Path $output 'OBSVCNameManager.exe')
