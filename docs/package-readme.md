# 日迹 Windows 预览版

解压后运行 Riji.Desktop.exe。需要 Windows x64 和 Microsoft Edge WebView2 Runtime；缺少后者可运行 install-webview2.ps1。包内已包含 .NET 运行时。

本包默认使用正式数据目录 %LOCALAPPDATA%\Riji\Production，与源码开发版分开。关闭窗口会隐藏到托盘，完全退出请在设置中操作。

可运行 install-desktop.ps1 安装到用户目录。升级、备份与恢复请保留脚本输出的路径和备份信息；不要覆盖运行中的程序或数据库。

Chrome / Edge 在扩展管理页加载 browser-extension 目录。Firefox 通过 about:debugging 临时加载 browser-extension-firefox/manifest.json，长期安装需要签名。扩展环境选择“正式版”，无需配对码。

网页标题当前默认开启，可在日迹设置中关闭。AI 功能需要配置服务地址、密钥与模型；截图和相关记录会发送给该服务。

THIRD-PARTY.txt 和 ThirdParty 目录包含第三方许可；files.sha256 是文件校验清单。build-info.json 记录构建提交和预览通道。本包属于预览构建。
