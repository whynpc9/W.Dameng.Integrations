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

Release 实库验收为 **5 通过，0 失败，0 跳过**。初始迁移创建的三张表均位于独立表空间。两个节点竞争同一周期实例时只有一个入队成功，直接调用 `NoUpdate()` 得到插入 1 行、重复键 0 行。时间任务竞争保留并验证了相差 1 tick 的 `UpdatedAt`。96,002 字节请求与 48,004 个 UTF-16 代码单元的异常消息均按原值读回。重启后，时间任务和周期实例各执行两次，最终持久化为 `Failed`，另一节点的进行中任务仍保留原锁。
