# 部署与迁移

## 安装包选择

- `SeewoAutoLogin_Setup_<版本>.exe`：轻量版，首次运行按需安装 WebView2。
- `SeewoAutoLogin_Setup_<版本>_WithWebView2.exe`：内置 WebView2 运行时，适合不能联网的教室一体机。

## 静默安装

```powershell
# 轻量版静默安装
.\SeewoAutoLogin_Setup_1.13.0.exe /SILENT /CLOSEAPPLICATIONS /NORESTART

# 指定安装目录
.\SeewoAutoLogin_Setup_1.13.0.exe /SILENT /CLOSEAPPLICATIONS /NORESTART /DIR="C:\Program Files\SeewoAutoLogin"
```

安装程序需要管理员权限；静默升级完成后会自动以 `--elevated --minimized --updated` 重新启动。

## 批量部署

1. 用管理权限在目标机器运行静默安装。
2. 首次启动后完成账号录入，或使用 `设置 -> 数据与迁移` 的加密导出/导入。
3. 如需统一配置，把 `%LOCALAPPDATA%\SeewoAutoLogin\config.json` 和 `credential.key` 一起分发；
   注意 `credential.key` 受 DPAPI 保护，只能在同一 Windows 用户下使用。
4. 开机自启由程序内的开机自启开关创建计划任务，安装器不写注册表启动项。

## 卸载与迁移

- 必须运行安装目录里的卸载程序，或执行 `SeewoAutoLogin.exe --uninstall`。
  只有卸载流程会清理计划任务、hosts 映射和本地数据。
- 设置里可以开启退出时恢复 hosts，正常退出时会移除本程序的 hosts 映射。
- 直接删除程序目录不会触发清理，可能残留 hosts 映射与计划任务。

## 升级注意

- v1.13.0 起凭据使用 `v2:` 格式。升级后不要再用旧版本运行，旧版本可能损坏 v2 密文。
- 静默更新下载完成但未安装成功时，下次启动会自动续装。