# 浏览器扩展

支持 Chrome、Edge 和 Firefox，使用同一份 worker 和选项页。扩展连接本机日迹，无需配对码。

## 开发加载

Chrome / Edge：打开扩展管理页，开启开发者模式，加载仓库 browser-extension 目录。

Firefox：

```powershell
node scripts/prepare-browser-extension.cjs firefox
```

打开 about:debugging 的“此 Firefox”，临时加载 artifacts/extensions/firefox/manifest.json。临时扩展需要在浏览器重启后重新加载；正式长期安装仍需相应签名和分发流程。

在扩展选项中选择环境：源码开发版选“开发版”，发布包默认选“正式版”。默认连接正式环境。开发端口为 4177，正式端口为 4178；不会自动尝试其他环境。

## 网站统计

在日迹设置中增改启用的网站归属规则。命中规则的网站记录为独立应用，未命中时间归入浏览器。规则只影响未来记录，历史名称和归属保留。

网页标题按桌面设置上报，当前桌面默认开启；可关闭。扩展不记录完整网址、查询参数、正文或网页摘要片段。

## 发布包

Chrome / Edge 加载包内 browser-extension；Firefox 临时加载 browser-extension-firefox/manifest.json。

无法连接时，检查日迹是否正在运行、环境是否匹配、本机端口是否被占用；源码更新后重新加载扩展，并点击选项页的“重新连接”。
