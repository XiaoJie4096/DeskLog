# 日迹

日迹是一款面向个人的 Windows 电脑活动记录与回顾工具。支持前台应用计时、网站独立归属、截图识别、时段摘要、AI 总结与对话，以及本地备份。

## 开发运行

需要 Windows x64、.NET SDK 10.0.300（允许同系列补丁）、Node.js 22.12 或更新版本，以及 Microsoft Edge WebView2 Runtime。

双击根目录的 **启动开发版.cmd**，会先退出开发实例、构建当前源码，再启动开发版。失败时窗口保留错误信息。

也可在 PowerShell 中执行：

```powershell
./scripts/build-desktop.ps1 -Test
./scripts/start-desktop.ps1
```

开发数据位于 `%LOCALAPPDATA%\Riji\Development`，正式数据位于 `%LOCALAPPDATA%\Riji\Production`。两者分离，源码目录移动不改变数据位置。

## 目录

- `src/Riji.Core`：计时、状态、归属规则与领域模型。
- `src/Riji.Infrastructure`：SQLite 存储、AI 请求、任务及数据维护。
- `src/Riji.Windows`：Windows 前台与系统事件集成。
- `src/Riji.Desktop`：WPF 容器、WebView2 桥接及本机扩展服务。
- `src/Riji.Web`：React / TypeScript 界面。
- `browser-extension`：Chrome、Edge、Firefox 扩展共用源码。
- `assets/icons`：应用图标。
- `tests`：领域、存储、界面数据和扩展测试。
- `scripts`：构建、启动、扩展准备、打包与恢复工具。
- `docs`：当前功能与维护文档。

## 文档

[开发说明](docs/development.md) · [架构](docs/architecture.md) · [隐私与数据](docs/privacy.md) · [浏览器扩展](docs/browser-extension.md) · [备份与恢复](docs/backup-and-restore.md)

第三方依赖及许可见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。本项目自身的开源许可证尚未选定。
