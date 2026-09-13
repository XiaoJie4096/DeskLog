# 开发说明

## 环境与构建

使用 Windows x64、PowerShell、.NET SDK 10.0.300、Node.js 22.12+ 和 WebView2 Runtime。SDK 约束见根目录 global.json。

```powershell
./scripts/build-desktop.ps1 -Test
```

脚本安装锁定的前端依赖，先构建前端，再构建 .NET 工程，最后运行 Node 与 .NET 测试。桌面项目在构建开始时收集前端产物，因此顺序不能颠倒。

双击根目录启动脚本可重建并运行最新开发版。该脚本先请求开发实例正常退出，超时会停止，不强制终止进程。关闭窗口通常会隐藏到托盘；完全退出请使用设置中的退出操作。

仅运行已有构建：

```powershell
./scripts/start-desktop.ps1
```

## 数据隔离

默认开发进程使用 %LOCALAPPDATA%\Riji\Development。带 --production 参数的进程使用 Production。发布目录的 production-default 标记也会选择 Production。

集成验证使用 --data-dir 指定独立临时目录，不应指向日常数据。系统边界测试可能锁定或挂起 Windows，应在明确安排后运行，不能并入普通构建。

开机自启使用当前可执行文件路径；迁移或更换启动版本后，应在设置中核实自启配置。开发版与正式版共用日迹自启项。

## 构建交付包

稳定版安装器使用 Inno Setup 生成：

```powershell
./scripts/build-stable-release.ps1
```

输出为 `artifacts/releases/DeskLog-Setup-v0.1.0.exe`。安装器使用固定应用标识，升级沿用安装目录；安装前要求日迹退出，不自动创建数据备份。安装目录包含 `extensions/chrome-edge` 和 `extensions/firefox` 两个未打包扩展目录。

使用 `scripts/build-private-production-test.ps1` 可生成只在本机或私有渠道使用的正式数据测试安装器，例如 `DeskLog-Setup-Private-v0.1.0.9001.exe`。它与稳定版使用相同的应用标识、安装目录和 `%LOCALAPPDATA%\Riji\Production` 数据目录，会覆盖当前安装程序文件但保留数据。稳定版安装器允许从测试版本降级覆盖回来。私有测试包不应上传到公开 Release。

在工作区已提交且干净时运行：

```powershell
./scripts/package-desktop.ps1
```

输出到 artifacts/packages，包含自包含运行时、扩展、许可、安装恢复脚本及 SHA-256 清单。当前构建通道为 preview，构建成功不代表已经完成整机验收。

打包和运行脚本的 .NET restore 可能更新依赖锁文件；提交前应检查 diff，勿跳过依赖变化审查。
