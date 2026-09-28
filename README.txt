OBS Virtual Camera Name Manager
Version 1.0.0

用途
快速修改 Windows 中 OBS Virtual Camera 的显示名称，并支持恢复原始名称。

系统要求
Windows 10 或 11，x64，.NET Framework 4.x，已安装 OBS Studio 虚拟摄像头组件。

使用方法
1. 双击 OBSVCNameManager.exe。
2. 输入新的设备名称，点击“修改名称”。
3. 按提示授权管理员权限；取消授权不会开始写入。
4. 修改完成后，重新打开需要使用摄像头的软件，以便它重新读取设备列表。

恢复
点击“恢复原始名称”。首次修改前，程序会自动备份已确认的原始配置。

说明
程序只处理已确认属于 OBS Virtual Camera 的显示名称配置。遇到名称不一致、备份损坏或无法确认的多个候选时，会停止相应修改。修改期间会逐项读回验证；出错时会尝试回滚。
程序不会修改 OBS 核心文件，不安装、禁用或删除设备，也不会强制重新枚举设备。已打开的软件可能缓存旧名称，请关闭后重新打开。

便携版的备份和日志位置
备份：%LOCALAPPDATA%\OBSVCNameManager\backup.json
日志：%LOCALAPPDATA%\OBSVCNameManager\logs\app.log
它们不会存放在 exe 所在目录。可以通过程序右上角的菜单打开对应文件夹。

项目主页
PROJECT_URL（待填写）

许可
当前项目尚未指定开源许可证。
