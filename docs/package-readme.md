# 日迹 Windows 0.1.4

使用 DeskLog-Setup-v0.1.4.exe 安装。需要 Windows x64 和 Microsoft Edge WebView2 Runtime；缺少后者可运行 install-webview2.ps1。安装器内已包含 .NET 运行时。

本包默认使用正式数据目录 %LOCALAPPDATA%\Riji\Production，与源码开发版分开。关闭窗口会隐藏到托盘，完全退出请在设置中操作。

安装器升级时沿用原安装目录，要求日迹正常退出，不自动创建数据备份。正式数据目录保持为 %LOCALAPPDATA%\Riji\Production。

Chrome / Edge 在扩展管理页加载安装目录中的 extensions\chrome-edge。Firefox 140 及以上版本打开 about:addons（扩展管理），点击右上角齿轮，选择“从文件安装附加组件”，选取 extensions\firefox\riji-firefox.xpi，确认权限与数据使用提示即可安装，重启浏览器无需重新安装。扩展默认连接正式版，无需配对码。

网页标题当前默认开启，可在日迹设置中关闭。AI 功能需要配置服务地址、密钥与模型；截图和相关记录会发送给该服务。

THIRD-PARTY.txt 和 ThirdParty 目录包含第三方许可。安装目录的 extensions 文件夹内提供 Chrome / Edge 的未打包扩展和 Firefox 的 Mozilla 签名扩展。
