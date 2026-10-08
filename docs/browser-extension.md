# 浏览器扩展

支持 Chrome、Edge 和桌面 Firefox 140 及以上版本，使用同一份 worker 和选项页。扩展连接本机日迹，无需配对码。

## 开发加载

Chrome / Edge：打开扩展管理页，开启开发者模式，加载仓库 browser-extension 目录。

Firefox：

```powershell
node scripts/prepare-browser-extension.cjs firefox
```

开发调试时打开 about:debugging 的“此 Firefox”，临时加载 artifacts/extensions/firefox/manifest.json。临时扩展需要在浏览器重启后重新加载；正式安装使用下文的签名 XPI。

在扩展选项中选择环境：源码开发版选“开发版”，发布包默认选“正式版”。默认连接正式环境。开发端口为 4177，正式端口为 4178；不会自动尝试其他环境。

## 网站统计

在日迹设置中增改启用的网站归属规则。命中规则的网站记录为独立应用，未命中时间归入浏览器。规则只影响未来记录，历史名称和归属保留。

网页标题按桌面设置上报，当前桌面默认开启；可关闭。扩展不记录完整网址、查询参数、正文或网页摘要片段。

Firefox 使用安装时的内置数据同意提示，统一声明必需的 `browsingActivity`（网站名称）和 `websiteContent`（网页标题）。不另设标题可选授权或同意页面，标题是否附带仍由桌面设置决定。安装授权不改变桌面默认值或已有用户设置。

扩展设置页提供随包附带的隐私说明：信息交给本机日迹，启用桌面 AI 功能后，相关记录还可能发送给用户配置的 AI 服务。

## 发布包

Chrome / Edge 加载安装目录中的 `extensions/chrome-edge`。Firefox 在 `about:addons`（扩展管理）点击右上角齿轮，选择“从文件安装附加组件”，选取安装目录中的 `extensions/firefox/riji-firefox.xpi`，确认 Firefox 的权限和数据使用提示。支持桌面 Firefox 140 及以上版本，重启浏览器无需重新安装。

仓库 `browser-extension/signed/riji-firefox.xpi` 保存 Mozilla 返回的 0.1.0 签名文件，打包时原样复制，不解压重打包。发布脚本先核对 XPI 校验和、签名文件是否齐全及包内源码是否与当前源码一致；签名信任、安装授权和浏览器重启后的连接仍须实际安装测试。

更新扩展时，运行 `powershell -ExecutionPolicy Bypass -File scripts/package-firefox-extension.ps1` 生成供 Mozilla 提交的未签名 ZIP，递增扩展版本并重新签名。更新签名文件及校验和后才能发布。签名步骤及审核说明见 [Firefox 签名说明](firefox-signing.md)；未签名 ZIP 不可当作正式安装包。

无法连接时，检查日迹是否正在运行、环境是否匹配、本机端口是否被占用；源码更新后重新加载扩展，并点击选项页的“重新连接”。
