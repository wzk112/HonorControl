# 安装与迁移

Honor Control 0.14.3 适配 Windows x64 的 HONOR MagicBook Pro 16 DRA-XX，固件写入已限定 BIOS 1.13。色彩校准文件对应本机内屏。完整安装目录约占用数百 MB，需保留整个解压目录。

## 安装与升级

1. 从 Releases 下载 HonorControl-0.14.3-Setup-x64.exe，双击并授权一次 UAC。
2. 点击安装，等待完成。安装到 C:\Program Files\HonorControl，注册当前用户的托盘和设备控制登录任务。
3. 从开始菜单打开。首次前台打开显示教程及官方组件迁移清单，登录后台启动不会打断用户。
4. 在关于页切换简体中文 / English，点击重新打开应用语言。升级重复运行新版安装器，保留偏好、日志和已有校准数据。
5. 通过 Windows「已安装的应用」中的 Honor Control 卸载。卸载保留个人数据、备份和硬件驱动；校准缓存保存在 LocalAppData\HonorControl\Calibration，重装时自动使用。

公共安装包不分发原厂 ICC/LUT。色彩切换需要匹配物理内屏的 HonorNative.icc、HonorsRGB.icc、HonorP3.icc、sRGB.lut 和 P3.lut。已有本机备份时，可在管理员 PowerShell 运行安装目录内的 Import-Calibration.ps1 -SourceDirectory 'D:\本机校准备份'，再到设置页安装 ICC。没有数据时可继续使用其他功能。当前没有通用电脑管家校准提取器。不要导入其他面板的数据。

开发者和手动包仍可使用 Install.ps1，普通用户直接运行 Setup。请使用日常登录的管理员账户，不要通过另一个账户的凭据安装。
## 原厂组件

原厂管家的性能、色彩和 OSD 后台可能与 Honor Control 同时修改设备设置。迁移时先安装并验证 Honor Control，再通过「设置 → 应用 → 已安装的应用」或「控制面板 → 程序和功能」卸载荣耀电脑管家。需要清除原厂提示时，检查发布者为荣耀的 OSD 组件。HUNTERCAMP、Magic 视界、YOYO 助理等按所需功能处理。 [微软卸载说明](https://support.microsoft.com/en-gb/windows/uninstall-or-remove-apps-and-programs-in-windows-4b55f974-2cc6-2d2b-d092-5905080eaf98) · [荣耀卸载说明](https://www.honor.com/cn/support/content/zh-cn15813919/)

需要继续保留以下硬件组件。

| 功能 | 依赖 |
| --- | --- |
| 固件模式、风扇、Fn 与充电保护 | 本机 ACPI/WMI、芯片组与固件 |
| 场景功耗 | Intel Dynamic Tuning |
| 色彩 | Intel 显卡驱动及对应内屏 LUT、ICC 校准文件 |
| 独显频偏与电源 | NVIDIA 显卡驱动 |
| 空间音频 | Honor Histen、Awinic 等音频 APO、校准文件与声卡驱动 |
| 单人、多人、环境拾音 | Fortemedia APO 与 Fortemedia APO Control Service |
| 声音与麦克风 | Realtek、Intel Smart Sound 及音频服务 |

荣耀名称也出现在固件、摄像头、音频效果等设备驱动中。删除驱动会影响对应硬件功能。系统目录中的残留文件应由对应卸载程序处理。

已卸载或停用后台后，可用随包脚本检查软件残留。默认只输出计划；管理员运行并明确传入 `-Apply` 才删除计划中的软件目录、服务、自启动、已知卸载项、应用包和应用注册表分支。

```powershell
.\Clean-HonorSoftware.ps1
.\Clean-HonorSoftware.ps1 -Apply
```

报告和注册表备份保存在 `%LOCALAPPDATA%\HonorControl\Migration`。脚本不删除 DriverStore、PnP 设备、`ProgramData\HonorEffect` 或音频驱动，不自动重启。失败与占用项会写入报告。

## 验证

- 重新登录后确认托盘与硬件控制已连接。
- 读取智能、高能、HUNTER 和风扇预设，HUNTER 的可选档位随供电条件变化。受限切换会显示原因浮窗。
- 用 Fn+P、Fn+R 验证模式与刷新率提示。触摸板、麦克风提示使用各自图标。音量和亮度使用 Windows 提示。
- 打开游戏后检查自动性能页的实际模式、目标模式和游戏进程。游戏进程存在时保持游戏模式，退出后保留 45 秒缓冲，再按场景稳定时间调整。
- 在音频页验证外放、耳机和拾音模式。耳机听感需连接对应设备检查，实际音效依赖驱动及音源。
- 色彩切换同时检查硬件 LUT 与 ICC。HDR、独显直连和外接多屏的校准适配范围见 README。

## 暂停与恢复

右键托盘选择退出，可暂停当前登录会话的常驻控制。关闭自动性能、OS Turbo 或显卡自动控制时，应用按恢复记录还原自己修改的设置。「恢复首次备份」用于恢复首次保存的硬件色彩及 ICC。

需要恢复原厂功能时，从荣耀官网按对应机型下载安装包和驱动。重启后检查音频、显示与性能状态，再决定启用哪个控制软件。 [荣耀驱动下载说明](https://www.honor.com/cn/support/content/zh-cn15816112)
