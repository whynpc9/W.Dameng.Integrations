# W.Dameng.Integrations

达梦数据库的第三方 .NET 库适配与实库兼容测试。

默认 EF 路径为 `W.EntityFrameworkCore.Dameng` + `W.EntityFrameworkCore.Dameng.WDriver` + `W.DamengProvider`，通过 `UseDamengWithWDriver` 注册。Hangfire 使用 W 驱动的 ADO.NET 存储。

## 集成范围

| 库 | 交付方向 |
| --- | --- |
| FlexLabs.EntityFrameworkCore.Upsert | 达梦 command runner |
| TickerQ.EntityFrameworkCore | 原生 EF 持久化与 Upsert 集成 |
| Hangfire | 达梦持久化存储 |
| OpenIddict.EntityFrameworkCore | 原生 Store 契约验证及必要的窄适配 |
| ASP.NET Core Identity | 原生 EF Store 兼容测试 |
| EFCore.NamingConventions | 命名、模型与迁移兼容测试 |

各集成独立放在 `src/`、`tests/` 和 `docs/`。具体支持版本、配置及验证边界以各集成文档为准。数据库提供程序本身（Npgsql、Pomelo 等）不在适配范围内。

## 验证要求

实库测试使用每个集成独享的用户/schema 和表空间，不复用已有业务或其他集成的测试 schema。连接资料仅保存在忽略的本地配置中，不输出到日志或提交到版本库。未配置实库时，跳过结果不能作为兼容性验收证据。
