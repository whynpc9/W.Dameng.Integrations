# TickerQ 达梦持久化

`W.TickerQ.Dameng` 适配 UniWeb 当前使用的 TickerQ 10.1.1。运行时仍由 TickerQ 的 EF provider 和 manager 完成入队、抢占、执行状态更新及恢复。适配层提供专用 DbContext、达梦类型映射和 FlexLabs runner 注册。

## 依赖

| 组件 | 本适配范围 |
| --- | --- |
| .NET | 10 |
| TickerQ、TickerQ.EntityFrameworkCore、TickerQ.Utilities | 10.1.1 |
| Microsoft.EntityFrameworkCore.Relational | 10.0.12，达梦 provider 的 NuGet 约束为 `[10.0.12, 11.0.0)` |
| W.EntityFrameworkCore.Dameng | 0.5.0 |
| W.EntityFrameworkCore.Dameng.WDriver | 0.5.0-preview.1 |
| W.DamengProvider | 0.3.1 |
| W.FlexLabs.Upsert.Dameng | 本仓库项目，使用 FlexLabs.EntityFrameworkCore.Upsert 10.1.0 |

TickerQ.EntityFrameworkCore 10.1.1 的 FlexLabs 依赖范围为 `[10.0.0-beta1, 11.0.0)`。运行连接由 `UseDamengWithWDriver` 创建。基础 EF provider 的 NuGet 依赖图包含 `DM.DmProvider`，实库测试的运行时连接和独立读回均使用 `W.DamengProvider`。

## 注册与迁移

```csharp
using TickerQ.DependencyInjection;
using W.TickerQ.Dameng;

builder.Services.AddDamengTickerQ(
    connectionString,
    options => options.ConfigureScheduler(scheduler => scheduler.NodeIdentifier = nodeId),
    migrationsAssembly: typeof(Program).Assembly.GetName().Name);

var app = builder.Build();
app.UseTickerQ();
```

定义 `[TickerFunction]` 的项目直接引用 `TickerQ` 10.1.1，以启用包内的源生成器。应用在 `UseTickerQ()` 前完成迁移。每个同时运行的副本使用不同的节点 ID，同一个副本重启时复用原 ID，以便原生启动恢复清理该节点遗留的锁。注册方法包含 `AddTickerQ` 和 `AddOperationalStore`。应用可通过可选回调继续配置 TickerQ 的调度、函数种子等原生选项。

迁移由应用管理。迁移项目的 `IDesignTimeDbContextFactory<DamengTickerQDbContext>` 应使用与运行时一致的选项，并将 `migrationsAssembly` 指向保存迁移的程序集。

```csharp
var options = new DbContextOptionsBuilder<DamengTickerQDbContext>();
options.UseDamengTickerQ(connectionString, typeof(TickerQDesignTimeFactory).Assembly.GetName().Name);
return new DamengTickerQDbContext(options.Options);
```

```sh
dotnet ef migrations add InitialTickerQ --context DamengTickerQDbContext
dotnet ef migrations script --context DamengTickerQDbContext
```

实库测试项目附有该模型生成的初始迁移和快照。表使用当前连接的 schema，保留 TickerQ 的三个表、索引和关系，包括周期实例的 `(CronTickerId, ExecutionTime)` 唯一索引。当前覆盖标准 `TimeTickerEntity` 与 `CronTickerEntity` 的专用 context 和空 schema 初始迁移。自定义实体、共享应用 context、其他 provider 数据导入及现有 TickerQ 表的原地升级需要单独适配与验证。

## 映射约定

- `RetryIntervals` 使用 CLOB 存储 JSON 整数数组。SQL NULL 与空数组 `[]` 分开保存。EF 使用序列比较和数组快照识别原位元素修改。
- `Request` 使用 BLOB。时间任务和周期定义各存自己的请求，周期实例仍通过原生 provider 读取周期定义的请求。
- `ExceptionMessage`、`SkippedReason` 使用 NCLOB。调度器生成的异常 JSON 保留原样。
- 时间字段使用 `TIMESTAMP(7)`，保留 100 ns 的 .NET tick。原生时间任务抢占使用 `UpdatedAt` 比较。
- 周期定义的 `Function` 和 `Expression` 分别映射为 `NVARCHAR2(256)` 和 `NVARCHAR2(512)`，供原生索引使用。时间任务和周期实例的 `LockHolder` 使用 `NVARCHAR2(256)`，供节点查找与抢占条件使用。

周期实例创建继承 FlexLabs runner 的事务表锁。写入同一张周期实例表时，即使业务键不同，也可能等待锁。[FlexLabs 适配说明](flexlabs.md)

## 实库验证

```sh
scripts/tickerq/run.sh provision
scripts/tickerq/run.sh test
```

脚本从授权文件读取管理员连接，每个 worktree 创建随机命名的达梦用户、schema 和专用默认表空间，并生成独立密码。隔离凭据保存在忽略的 `.local/tickerq/isolation.json`，Unix 权限为 `0600`。重复 provision 会核对原隔离实例。若创建中断，入口会拒绝复用未就绪记录，避免再次分配或误清理数据库对象。测试会校验当前用户、schema 和默认表空间，在已验证的三个专用测试表内重置数据。脚本中的 `PlaintextAllowed` 只用于此本地测试连接。

测试从原生 manager/provider 写入，并通过另一个连接直接执行 SQL 读回。双节点竞争采用独立 host 与连接，重启用例销毁旧 host 后以相同 ID 启动新 host，由 TickerQ 的原生启动回调和后台调度恢复任务。该测试边界是共享数据库的多个调度 host，未覆盖跨机进程终止、Redis 和 Dashboard。

初始适配的 Release 实库验收为 **5 通过，0 失败，0 跳过**。初始迁移创建的三张表均位于独立表空间。两个节点竞争同一周期实例时只有一个入队成功，直接调用 `NoUpdate()` 得到插入 1 行、重复键 0 行。时间任务竞争保留并验证了相差 1 tick 的 `UpdatedAt`。96,002 字节请求与 48,004 个 UTF-16 代码单元的异常消息均按原值读回。重启后，时间任务和周期实例各执行两次，最终持久化为 `Failed`，另一节点的进行中任务仍保留原锁。

## 时间任务抢占的时钟边界

TickerQ 10.1.1 的原生 provider 在以下两种时序中会向两个节点各返回一次同一时间任务。两个节点都先取得写入前的快照，第二次更新在第一次提交后执行。达梦通过独立连接读回每次更新后的 owner、status、`UpdatedAt` 和 `LockedAt`。

| 原生路径 | 实库结果 |
| --- | --- |
| `QueueTimeTickers`，固定时钟等于快照的 `UpdatedAt` | A 和 B 各领取 1 次。每次均写入 `Queued`，时间戳保持不变，owner 从 A 改为 B。 |
| `QueueTimedOutTimeTickers`，两节点时钟分别比快照早 1 tick 和 2 tick | A 和 B 各领取 1 次。每次均写入 `InProgress`，时间戳随节点时钟回退，owner 从 A 改为 B。 |

这是重复领取证据。测试直接枚举 provider 的返回值，未启动调度器执行这两项任务。原生更新在常规路径比较 `UpdatedAt == snapshot`，超时路径比较 `UpdatedAt <= snapshot`。两条路径在更新时均写入节点时钟，且没有重新检查任务状态或 owner。达梦 `TIMESTAMP(7)` 读回了完整的 100 ns tick，因此提高列精度无法使旧快照失效。[TickerQ 10.1.1 原生实现](https://github.com/Arcenox-co/TickerQ/blob/b2684f41a1c22f09e05cbac087c117751bd3e78c/src/TickerQ.EntityFrameworkCore/Infrastructure/BasePersistenceProvider.cs#L35-L99)

本适配继续使用原生 provider，运行时行为保持上述边界。节点各自使用单调时钟无法保证写入值严格大于另一节点已读到的值。数据库触发器如果单独改写时间戳，常规路径返回给调度器的 `UpdatedAt` 仍为原生代码写回的时钟值，与数据库状态不一致。原生实现类为 internal，两条领取方法均没有可覆盖的扩展点。修复需要介入原子领取条件或并发令牌协议，并重新验证旧 `Queued` 任务的超时接管、同节点重启恢复和返回对象与数据库状态的一致性。本轮尚无通过这些验证的局部修复或可确认的升级版本。升级验收要求候选版本在两种时序下均只产生一个赢家，并保留超时与重启恢复行为。

已发布的 10.5.1 是后续验证候选。其 EF 存储升级涉及运行分区和表结构迁移。另查到上游开发提交 `2ac18b3` 在持久化协议中使用获取令牌，但源码证据无法代替该发布包的达梦领取和迁移实测。[10.5.1 NuGet](https://www.nuget.org/packages/TickerQ.EntityFrameworkCore/10.5.1) · [存储升级说明](https://tickerq.net/docs/5.x/guides/storage-upgrade) · [上游开发提交的 EF provider](https://github.com/Arcenox-co/TickerQ/blob/2ac18b3b38150e52c1bdb5581a43b98aa569b73d/src/TickerQ.EntityFrameworkCore/Infrastructure/BasePersistenceProvider.cs)

F14 的 Release 定向测试为 **2 通过，0 失败，0 跳过**。最终全套为 **7 通过，0 失败，0 跳过**。其中新增两项的通过表示成功复现并读回重复领取。前两轮全套分别为 5 通过、2 失败和 6 通过、1 失败，均无跳过。三个失败均为既有用例遇到的 WDriver 超时，最终原样重跑通过。
