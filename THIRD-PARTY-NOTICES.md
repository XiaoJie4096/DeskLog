# Third-party notices

日迹使用第三方依赖。其许可和版权声明独立于本项目自身的许可证，不因本项目的分发方式而失效。

| 依赖 | 许可或获取位置 |
| --- | --- |
| React、React DOM、Scheduler | MIT；npm 包内 LICENSE |
| TypeScript、Vite 及构建依赖 | 对应 npm 包内 LICENSE 和包元数据 |
| Microsoft .NET / WPF / ASP.NET Core 运行时 | 对应运行时包内 LICENSE、THIRD-PARTY-NOTICES |
| Microsoft.Web.WebView2 | NuGet 包内 Microsoft 许可条款 |
| Microsoft.Data.Sqlite、SQLitePCLRaw 及传递依赖 | 对应 NuGet 包内许可和 nuspec 元数据 |
| xUnit、Microsoft.NET.Test.Sdk 等测试依赖 | 对应 NuGet 包内许可和元数据 |

精确版本以 package-lock.json 和各项目 packages.lock.json 为准。

发布脚本收集实际 .NET 依赖及运行时包的原始许可和元数据，并将 React、React DOM、Scheduler 的原始许可复制到发布包 ThirdParty 目录，同时生成 THIRD-PARTY.txt。分发时应保留这些文件。

[Apache License 2.0 全文](docs/licenses/Apache-2.0.txt)供适用依赖使用，不表示将本项目许可为 Apache-2.0。发布前须根据实际依赖核对全部许可要求。
