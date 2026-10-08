# Firefox 扩展签名说明

## 当前签名文件

用户已从 Mozilla 开发者中心下载通过审核的 0.1.0 XPI，仓库保存为 `browser-extension/signed/riji-firefox.xpi`。SHA256：`25C0A94BB231F9C5F8B42D3B89A09834C1FD8F109B3B63438DEDC2EC46877A0D`。

已核对包内 Mozilla 签名文件、扩展 ID、版本、数据授权声明及与提交源码的一致性。发布构建将文件原样复制到 `extensions/firefox/riji-firefox.xpi`，设置页指引从文件安装。尚需正式 Firefox 实际安装以验证签名信任、数据授权、连接及重启后持久加载；静态检查不能替代这些验证。

发布前运行 `powershell -ExecutionPolicy Bypass -File scripts/verify-firefox-extension.ps1`。两种桌面发布脚本也会自动执行此检查。修改共享扩展源码后，检查会阻止继续分发过期 XPI；必须递增扩展版本、重新签名，然后更新签名文件和 `.sha256` 校验文件，不能修改签名包内部内容。仅调整桌面程序或提交说明无需重新签名。

## 当前方案

- 分发方式：Mozilla 自分发签名（提交时选择 **On your own**），不申请 AMO 商店上架。
- 最低版本：桌面 Firefox **140.0**，依靠安装时的内置数据同意提示。不支持旧版本，不另加同意页面。
- 必需数据声明：`browsingActivity` 与 `websiteContent`。不设置可选标题授权，实际标题上报由日迹的“记录网页标题”开关决定。
- 扩展 ID：`riji-browser@riji.local`，保持不变。首次提交包版本为 `0.1.0`；如果这个版本已经提交过签名，重新提交需递增扩展版本。
- 本次不调整桌面版本、用户配置或默认值，不增加自动更新地址。

## 生成并检查提交包

在仓库根目录运行：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/package-firefox-extension.ps1
node --test tests/browser-extension.test.cjs
npx --yes web-ext@10.7.0 lint --source-dir artifacts/extensions/firefox --self-hosted
```

上传文件：`artifacts/extensions/riji-firefox-0.1.0.zip`。同目录的 `.zip.sha256` 文件记录校验值。ZIP 根目录直接包含 `manifest.json`、共享脚本、设置页、隐私说明和 MIT 许可，没有外层文件夹或 Chromium manifest。

源码为原生 JavaScript、HTML 和 CSS，没有转译、压缩混淆或外部依赖。准备脚本仅复制文件并将 `manifest.firefox.json` 改名为 `manifest.json`；打包脚本仅将固定文件列表压缩成 ZIP。扩展源文件就在提交包内，无需单独构建源码包。若审核要求完整仓库或桌面测试包，再按要求提供。

自动检查不能替代实际安装授权、签名和浏览器联调。

`web-ext 10.7.0` 会给出一条 Android 最低版本警告（`KEY_FIREFOX_ANDROID_UNSUPPORTED_BY_MIN_VERSION`）：Android 的内置数据授权最低要求 142。此包配合 Windows 桌面程序，未声明 `gecko_android`，按 Mozilla 的规则仅提供桌面 Firefox，因此保留已确认的桌面最低版本 140。这条 Android 警告不表示桌面版本不支持授权；其他错误或警告仍需逐项排查。

## 可用于提交表单的隐私说明

> 日迹扩展配合本机日迹桌面程序统计网站使用时间。它读取当前使用的普通网页，将网站名称（如 example.com）发送到本机日迹；按桌面程序的“记录网页标题”设置，可同时发送最多 200 个字符的网页标题。标题默认开启，可在日迹设置中关闭。
>
> 扩展不发送完整网址、网址路径或查询参数、网页正文和网页摘要片段，不记录隐私窗口、浏览器内部页面或后台窗口中的网页。扩展同时向本机发送浏览器类型及连接校验会话信息，在浏览器本地保存连接环境、连接状态和最近连接时间。
>
> 扩展直接连接本机 127.0.0.1，不向开发者服务器发送信息，也不直接连接 AI 服务。日迹桌面程序把记录保存在本机。启用日迹 AI 功能后，相关记录（可能包括网站名称和网页标题）可作为截图识别、摘要、总结或对话上下文发送给用户配置的 AI 服务，其处理受该服务隐私政策约束。
>
> Firefox 安装时请求浏览活动和网站内容的必需数据授权。不同意可取消安装；停用或移除扩展可停止发送网页信息。已保存记录可在日迹的数据设置中清理。扩展无需注册或登录，隐私说明可在扩展设置页查看。

如果提交表单要求公开隐私政策 URL，需要把以上说明发布到自己可维护的公开页面后填写真实地址。随包 `privacy.html` 是可离线查看的说明，不能冒充公开 URL。

## 可用于审核备注的英文说明

> Riji Website Time is a companion extension for the Riji Windows desktop time tracker. Its primary purpose is to attribute time spent on configured websites to separate activities in the desktop app. It actively processes website information; it is not merely a launcher for another app.
>
> The extension requires desktop Firefox 140 or newer. It uses Firefox's built-in installation consent and declares both browsingActivity (the current website hostname) and websiteContent (the current tab title) as required data types. There is no separate optional title permission. The desktop app controls whether titles are included, using its "Record webpage titles" setting, which defaults to enabled. Disabling this setting suppresses titles in subsequent reports.
>
> Only a focused, non-private HTTP(S) page is reported. No full URL, path, query string, page body or description snippet is transmitted. Reports include the hostname, an optional title limited to 200 characters, browser type, and local session/protocol metadata. The extension stores the selected environment and connection status locally. It does not contact developer servers or AI providers directly. The desktop app can pass activity records, potentially including titles, to the AI provider configured by the user when AI features are used. This is disclosed on the options page and in the bundled privacy.html.
>
> To test: run the Riji Windows desktop application, start Firefox, install the extension, accept its required data consent, and open a normal website. No extension account or login is needed. The extension defaults to the production desktop on http://127.0.0.1:4178; source development builds use port 4177 and require selecting the development environment in the extension options. The action button opens the options page with connection status and a reconnect button. Configure a website attribution rule in the desktop browser settings to view separate website time. Toggle "Record webpage titles" in the desktop app to verify title control. Private windows and browser-internal pages are excluded.
>
> The submitted archive contains the complete, readable extension source (plain JavaScript, HTML and CSS), with no obfuscation, third-party libraries or compilation step. Packaging copies the shared files and renames manifest.firefox.json to manifest.json. The license is MIT. This submission is for self-distribution ("On your own").

提交前需附上可供审核测试的 Windows 桌面程序下载地址或测试包及上述操作说明。当前文档未指定下载地址，不应编造；若审核需要，提供对应桌面构建产物。测试网站统计和标题开关无需 AI Key，测试 AI 功能另需自行配置 AI 服务。

## 实际提交与后续验证

1. 登录自己的 [Mozilla 开发者中心](https://addons.mozilla.org/developers/)，提交新扩展并选择 **On your own**。
2. 上传生成的 ZIP，补充描述、数据用途、隐私说明和审核备注，按页面要求补齐测试材料。
3. 自动验证或人工审核通过后，下载 Mozilla 返回的已签名 `.xpi`。ZIP 尚未签名，不能改后缀来代替 XPI；签名后的文件也不能自行修改内容。
4. 用正式 Firefox 安装已签名 XPI，核对内置数据授权、默认连接正式版、网站统计、标题开关、隐私窗口排除，以及浏览器重启后仍能加载连接。
5. 将验证通过的签名文件纳入安装包，并用从文件安装的正式指引分发。当前仓库已接入 0.1.0 XPI 和正式安装指引，正式发布前仍需完成第 4 步实测。

后续扩展更新需要递增扩展版本、重新签名。本次未配置自托管自动更新，用户需要安装后续签名版本。

## 官方依据

- [Firefox 内置数据同意机制](https://extensionworkshop.com/documentation/develop/firefox-builtin-data-consent/)
- [Mozilla 扩展审核政策](https://extensionworkshop.com/documentation/publish/add-on-policies/)
- [browser_specific_settings 声明](https://developer.mozilla.org/en-US/docs/Mozilla/Add-ons/WebExtensions/manifest.json/browser_specific_settings)
- [提交与自分发签名流程](https://extensionworkshop.com/documentation/publish/submitting-an-add-on/)
