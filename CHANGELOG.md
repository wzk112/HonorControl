# Changelog / 更新记录

## 0.14.4

- 安装与卸载窗口适配高 DPI 和较小屏幕。说明可滚动，底部按钮随窗口排版。
- Setup and uninstall windows adapt to high DPI and smaller screens, with scrollable instructions and a layout that keeps the action buttons visible.

## 0.14.3

- Runtime logs rotate at 2 MiB with three files per stream. Logs not updated for 14 days are removed. Recovery records and calibration backups are retained.
- 日志自动滚动并清理旧文件，恢复记录与校准备份保留。

## 0.14.2

- Single-file bilingual setup and uninstaller; preserves preferences and local calibration, registers sign-in startup.
- Specific OEM applications / service names and live service detection in the migration tutorial.
- Handles Fn+P denied-power event 0x2A6, including battery use; centers warning text; fixes the color-reapply allowlist after refresh-rate changes.
- 新增双语单文件安装与卸载程序、具体原厂服务迁移清单，修复拔电 Fn+P 提示与浮窗文字对齐。

## 0.14.1

- Centered the tutorial using the internal dialog width resource; improved theme contrast and expanded usage steps.
- Documented per-feature configuration support; limited the verified DTT policy table to 125H.
- Kept network profile names and identifiers unchanged when translating status messages.
- 修复教程居中与说明文字对比度，增加各功能使用步骤和同型号不同配置的支持说明。

## 0.14.0

- First-launch tutorial, update notes and About page; foreground opens only.
- Chinese and English UI, including settings, status, tray and notifications.
- Slower native page transitions: 320 ms fade and 380 ms translation, following Windows animation preferences.
- Twelve additional game process rules including CS2, League of Legends, PUBG, CrossFire, Genshin Impact and Star Rail. Excludes launchers and common helper processes from automatic game sessions.
- Installation and migration guides; auditable OEM software cleanup script; public builds omit device calibration data and support local import.

- 新增首次启动教程、更新记录、关于页及中英文界面。
- 页面动画放慢，保留 Windows 关闭动画时的即时切换。
- 补充常见游戏主进程，排除启动器和辅助进程误触发。
- 提供安装迁移指南、原厂软件清理脚本与本机校准导入工具。

## 0.13.7

- Histen speaker / headphone settings and Fortemedia single-person, group and ambient microphone pickup modes.
- Function-specific OSD icons and reasons for denied performance changes.
- Read-back HUNTER fan levels; rejected manual changes preserve automatic mode.
- Automatic performance keeps a recognized running game across focus changes, with a 45-second process-gap grace period.
- Fixed nullable JSON status parsing and added WinUI navigation transitions.
