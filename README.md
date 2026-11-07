# W.Dameng.Integrations

达梦数据库的第三方 .NET 库适配与实库兼容测试，目标框架为 .NET 10。

默认 EF 路径为 `W.EntityFrameworkCore.Dameng` 0.5.0 + `W.EntityFrameworkCore.Dameng.WDriver` 0.5.0-preview.1 + `W.DamengProvider` 0.3.1，通过 `UseDamengWithWDriver` 注册。EF Core 验收版本为 10.0.12。WDriver 扩展目前使用预发布包。

| 集成 | 交付 | 接入与验收说明 |
| --- | --- | --- |
| FlexLabs.EntityFrameworkCore.Upsert 10.1.0 | `W.FlexLabs.Upsert.Dameng`，达梦 command runner | [FlexLabs](docs/flexlabs.md) |
| TickerQ.EntityFrameworkCore 10.1.1 | `W.TickerQ.Dameng`，原生 EF 持久化与 Upsert 集成 | [TickerQ](docs/tickerq.md) |
| Hangfire.Core 1.8.23 | `W.Hangfire.Dameng`，基于 W 驱动的 ADO.NET 存储 | [Hangfire](docs/hangfire.md) |
| OpenIddict.EntityFrameworkCore 6.3.0 | `W.OpenIddict.Dameng`，事务、撤销与清理的窄适配 | [OpenIddict](docs/openiddict.md) |
| ASP.NET Core Identity 10.0.12 | 保留原生 EF Store，提供兼容测试与接入示例 | [Identity](docs/identity.md) |
| EFCore.NamingConventions 10.0.1 | 保留原生命名约定，提供模型与迁移兼容测试 | [NamingConventions](docs/namingconventions.md) |

Npgsql、Pomelo 等其他数据库提供程序不做达梦适配。UniWeb 接入时应同步调整其集中管理的 EF Core 版本；本仓库未修改 UniWeb 或达梦 provider 仓库。

## 构建与测试

安装 .NET SDK 10.0.401 后，在仓库根目录构建全部运行时和测试项目：

```sh
scripts/build.sh
```

实库测试需要先使用各集成的 `provision` 入口创建独立用户/schema 和表空间，再运行测试。授权的本地管理员配置路径及隔离规则见 [AGENTS.md](AGENTS.md)。每个入口会保存自己的忽略配置，重复 provision 验证已有分配，不复用来源文件中的原测试 schema。

```sh
scripts/flexlabs/run.sh provision
scripts/flexlabs/run.sh test -c Release
scripts/tickerq/run.sh provision
scripts/tickerq/run.sh test -c Release
scripts/hangfire/run.sh provision
scripts/hangfire/run.sh test
OPENIDDICT_DAMENG_TEST_TRANSPORT=PlaintextAllowed scripts/openiddict/run.sh provision
scripts/openiddict/run.sh test
scripts/identity/provision.sh
scripts/identity/test.sh -c Release
scripts/namingconventions/run.sh provision
scripts/namingconventions/run.sh test
```

`PlaintextAllowed` 仅用于本地验收实例，应用端按自己的传输安全配置连接。测试缺少隔离配置时应失败，不能把跳过当作兼容性证据。同一份数据库配置串行执行测试；测试会重置各自夹具的数据。

## 支持边界

- FlexLabs 使用排他表锁，同表写入会串行等待；支持 `Run`/`RunAsync`，不支持 `RunAndReturn`。
- Hangfire 的存储写事务由同一 schema 的 `WRITER` 行锁串行化；作业执行采用至少一次语义，业务副作用需要幂等处理。提交应答丢失未做实库故障注入。
- OpenIddict 覆盖默认字符串主键模型，维护操作使用专用作用域；OAuth/OIDC HTTP 认证流程未纳入存储层验收。
- Identity 覆盖 Schema Version 2 和字符串主键；Passkey、端到端登录和既有库升级未纳入验收。
- NamingConventions 已验证当前 schema 的幂等脚本和运行时迁移。显式 history schema 的幂等脚本仍受 provider 0.5.0 限制，可使用已验证的运行时 `Migrate`。

各文档列明具体版本、实库条件及未验收范围。测试数据库位于同一达梦实例内，通过独立用户/schema、表空间和随机凭据隔离。实施与合并记录见 [验收记录](docs/implementation-tracking.md)。
