# 备份与恢复

日常备份从“设置 → 数据与备份 → 导出备份”操作。备份包含记录、分类、预设、总结、对话和仍保留的关联截图，不包含 API Key。

“导入并替换”会完整替换数据，并先自动备份原数据。维护期间暂停采集、取消进行中的 AI 任务；导入或清空后自动记录及截图保持关闭，请检查内容后再开启。

源码位置与数据位置独立：开发数据在 %LOCALAPPDATA%\Riji\Development，正式数据在 Production。不要将运行中数据库的单个文件当成完整备份；优先使用应用内导出。

## 安装升级备份

发布包的 install-desktop.ps1 在管理安装升级时生成数据快照。保留脚本输出的备份目录及元数据。

restore-upgrade-backup.ps1 接受 -BackupDirectory 和 -DestinationDirectory，将经校验的快照恢复到独立目录。rollback-desktop.ps1 接受 -BackupDirectory 和 -DataDirectory，用于管理安装的回滚；中断恢复使用 -Resume。执行前退出目标环境的日迹，核实路径和备份。

应用检测到未完成恢复标记时会拒绝启动，应完成恢复流程，不要直接删除标记绕过检查。
