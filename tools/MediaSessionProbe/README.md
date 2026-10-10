# 视频播放检测工具

独立检测 Windows 媒体会话和前台应用，每 5 秒刷新。不引用日迹项目，不控制播放，不修改日迹的设置或数据。

构建：

```powershell
dotnet publish tools/MediaSessionProbe/MediaSessionProbe.csproj -c Release -o artifacts/media-session-probe
```

双击 `artifacts/media-session-probe/MediaSessionProbe.exe`。运行需要 .NET 10 Windows 桌面运行时。

播放视频后切回浏览器或播放器，等至少 5 秒，再返回检测工具。点击“复制最近结果”可查看最近 12 次检测，包括切回检测工具前的前台应用。工具自身在前台时显示不匹配是正常现象。

建议依次验证：浏览器前台播放、暂停、切到记事本、Chrome 播放时切到 Edge、本地播放器前台播放、音乐播放。

“符合视频条件”为：Windows 明确报告 `Playing`、`Video`，且来源与前台进程名或进程 AppUserModelID 完全相同。缺失或无法映射的来源不会猜测；表格会显示判断依据。这是应用级匹配，不能区分同一浏览器的窗口和标签页。部分桌面应用的来源可能来自窗口级标识而非进程标识，工具会如实显示未匹配或无法确认，供后续判断是否需要增加映射。

也可保存一次原始检测结果：

```powershell
artifacts/media-session-probe/MediaSessionProbe.exe --snapshot "$PWD/artifacts/media-session-probe/snapshot.json"
```

媒体状态、类型使用 Windows API 原始枚举名称，便于确认实际报告内容。不采集媒体标题和网页内容；界面的最近结果仅保存在内存，点击复制后才写入剪贴板。

v2 同时展示 `TryGetMediaPropertiesAsync().PlaybackType`（元数据上报类型）和 `GetPlaybackInfo().PlaybackType`（播放信息上报类型），两者均为 API 原始值。工具不分析画面，也不会将视频主动归类为音乐。“严格视频条件”仍以元数据上报 `Video` 为准，仅用于检验之前讨论的严格规则是否可行；新增字段用于对照，尚未选择新的判定策略。
