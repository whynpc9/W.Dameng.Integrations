# W.Dameng.Integrations

达梦数据库的第三方 .NET 库适配与实库兼容测试，面向 .NET 10。项目提供四个运行时适配包，以及 Identity、EFCore.NamingConventions 的原生组件兼容测试和接入示例。

默认 EF 路径为 `W.EntityFrameworkCore.Dameng` 0.5.0 + `W.EntityFrameworkCore.Dameng.WDriver` 0.5.0-preview.1 + `W.DamengProvider` 0.3.1，通过 `UseDamengWithWDriver` 注册；EF Core 验收版本为 10.0.12。Hangfire 使用 W 驱动的 ADO.NET 存储，不依赖 EF。

仓库内适配包版本为 **0.1.0-preview.1**，WDriver EF 扩展也处于预发布阶段。本次交付包含源码和本地打包验证，未发布到 NuGet.org。支持范围以以下固定版本和实库记录为准。

## 集成范围

| 第三方组件 | 交付内容 | 接入与验收说明 |
| --- | --- | --- |
| FlexLabs.EntityFrameworkCore.Upsert 10.1.0 | `W.FlexLabs.Upsert.Dameng`：MERGE、类型映射、事务和并发控制 | [FlexLabs](docs/flexlabs.md) |
| TickerQ 10.1.1 + 领取令牌补丁 | `W.TickerQ.Dameng`：专用 DbContext、Upsert 与三个 `W.TickerQ.*` 补丁依赖包 | [TickerQ](docs/tickerq.md) |
| Hangfire.Core 1.8.23 | `W.Hangfire.Dameng`：作业、队列、租约、锁、清理及监控存储 | [Hangfire](docs/hangfire.md) |
| OpenIddict.EntityFrameworkCore 6.3.0 | `W.OpenIddict.Dameng`：事务、级联删除、撤销与清理适配 | [OpenIddict](docs/openiddict.md) |
| ASP.NET Core Identity 10.0.12 | 保留原生 EF Store；验证 CRUD、关系、并发及锁定状态 | [Identity](docs/identity.md) |
| EFCore.NamingConventions 10.0.1 | 保留原生命名约定；验证模型、迁移与独立读回 | [NamingConventions](docs/namingconventions.md) |

Npgsql、Pomelo 等其他数据库提供程序不做达梦适配。UniWeb 接入时应同步调整其集中管理的 EF Core 版本；本仓库未修改 UniWeb 或达梦 provider 仓库。

## 构建与本地打包

需要 .NET SDK **10.0.401**、Git 和 Python **3.9+**。从仓库根目录执行：

```sh
# 从固定源码重建 TickerQ 补丁，再 Release 构建解决方案；不运行实库测试
scripts/build.sh

# 编译六个 provisioner、打包、独立消费与反向检查；不连接数据库
scripts/verify-packages.sh --negative-checks
```

包验证产物和报告位于 `artifacts/package-validation/<run-id>/`。它生成四个适配包及三个 TickerQ 补丁包，检查全库消费者和 Hangfire 单包消费者的实际依赖、包哈希和程序集，并确认错误包源、依赖降级及混装原版 TickerQ 会失败。具体阶段见 [包验证说明](docs/package-validation.md)。

应用可引用 `src/` 下对应项目，或把本次生成的包目录加入应用的 NuGet 源，并保留用于恢复上游依赖的包源。不要将测试项目作为运行时依赖。各库文档提供注册、配置和迁移示例。

例如，FlexLabs 与 WDriver 在同一个 DbContext 的选项中注册：

```csharp
using Microsoft.EntityFrameworkCore;
using W.FlexLabs.Upsert.Dameng;

options.UseDamengWithWDriver(connectionString).UseDamengUpsert();
```

## 实库验证

每个库先通过自己的 `provision` 创建独立用户/schema、独立表空间和随机凭据，再运行实际第三方 API 测试。当前验收使用同一个达梦实例中的六套隔离资源，并非六台独立服务器。测试会重置所属夹具的数据，同一份分配必须串行使用。

完整命令、隔离规则、结果文件和故障处理见 [测试复跑说明](docs/testing.md)。数据库凭据只保存在忽略的 `.local/`，不随源码提交。缺少实库配置时测试失败，不能把跳过当作兼容证据。

## 当前结果与限制

六个库均已完成独立 Sol High 实现和 Astra High 审查并合并。2026-11-08 首轮修复的各套件共 **82 个不同实库用例有通过记录**，其中含已知缺陷的刻画用例；这不是一次 82/82 的组合运行。Hangfire 完整轮次为 14 通过、1 失败，修订独立观察连接后定向复验 1 项通过；运行时 DLL 未变化。原始失败和重跑记录见 [执行汇总](docs/review-remediation-2026-11-08.md)。

后续 F13/F14 修复也已完成：OpenIddict 全量 30 项及定向 6 项通过，覆盖 32 个不同逻辑用例；TickerQ 最终 14 项检查通过，旧版播种与增量迁移各 1 项通过。计数、原始失败历史与验收边界见 [后续修复记录](docs/f13-f14-resolution-2026-11-08.md)。

- **TickerQ 使用领取令牌补丁。** 默认依赖三个 `W.TickerQ.*` 10.1.1-dameng.1 包；领取、开始和写回核对同一令牌。现有库须停旧节点并增加两列，再统一切换补丁包，禁止混装。业务外部副作用仍需幂等。见 [迁移与边界](docs/tickerq.md)。
- **OpenIddict 大图删除已分页。** 每页最多读取 16 行 ID 与并发戳，保留外键顺序、原始并发检查及同一事务的全图回滚；新增跟踪量受限，整图事务的锁和日志开销仍存在。HTTP OAuth/OIDC 流程不在存储层测试范围。
- FlexLabs 使用排他表锁，同表写入会等待；支持 `Run`/`RunAsync`，不支持 `RunAndReturn`。
- Hangfire 同 schema 写事务由 `WRITER` 行锁串行化；作业执行采用至少一次语义，业务副作用需幂等处理。提交应答丢失尚未故障注入验证。
- Identity 验证 Schema Version 2 和字符串主键；Passkey、端到端登录和既有库升级未验收。
- NamingConventions 已验证当前 schema 的幂等脚本和运行时迁移。显式 history schema 的幂等脚本仍受 provider 0.5.0 限制，可使用已验证的运行时 `Migrate`。

## 文档与目录

| 入口 | 内容 |
| --- | --- |
| [测试复跑说明](docs/testing.md) | 环境、各库命令、隔离与失败结果处理 |
| [包验证说明](docs/package-validation.md) | 不连接数据库的打包与真实 NuGet 消费检查 |
| [后续修复验收](docs/f13-f14-resolution-2026-11-08.md) | F13/F14 原因、修复、迁移及剩余边界 |
| [首轮执行汇总](docs/review-remediation-2026-11-08.md) | F01–F14 首轮处理、提交及测试历史 |
| [初始验收记录](docs/implementation-tracking.md) | 2026-11-07 初始实施与合并证据 |
| [仓库协作约定](AGENTS.md) | 构建、模型、测试归属和凭据处理约定 |

`src/` 保存四个适配库；`tests/` 保存六套实库测试；`scripts/` 保存构建、打包和隔离库入口；`docs/` 保存接入说明与验收记录。`.local/`、`artifacts/` 和编译输出均不纳入版本控制。
