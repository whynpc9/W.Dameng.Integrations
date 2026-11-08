# TickerQ 达梦持久化

`W.TickerQ.Dameng` 适配 UniWeb 当前使用的 TickerQ 10.1.1。默认发行路径使用从 10.1.1 固定源码构建的领取令牌补丁。调度器、manager 和 EF provider 保留 TickerQ 的原生入口，补丁在时间任务领取、执行和写回之间传递同一令牌。

## 依赖

| 组件 | 本适配范围 |
| --- | --- |
| .NET | 10 |
| W.TickerQ.Core、W.TickerQ.EntityFrameworkCore、W.TickerQ.Utilities | 10.1.1-dameng.1，基于上游 10.1.1 |
| Microsoft.EntityFrameworkCore.Relational | 10.0.12，达梦 provider 的 NuGet 约束为 `[10.0.12, 11.0.0)` |
| W.EntityFrameworkCore.Dameng | 0.5.0 |
| W.EntityFrameworkCore.Dameng.WDriver | 0.5.0-preview.1 |
| W.DamengProvider | 0.3.1 |
| W.FlexLabs.Upsert.Dameng | 本仓库项目，使用 FlexLabs.EntityFrameworkCore.Upsert 10.1.0 |

补丁包与原包的程序集名和命名空间一致。应用替换原版的三个包引用，三个补丁包使用同一版本。适配包的构建目标会拒绝混装原版程序集。运行连接由 `UseDamengWithWDriver` 创建。基础 EF provider 的 NuGet 依赖图包含 `DM.DmProvider`，实库测试的运行时连接和独立读回均使用 `W.DamengProvider`。

## 构建补丁包

从本仓库根目录运行 `scripts/tickerq/build-f14.sh`。脚本检出上游 10.1.1 的固定提交 `b2684f41a1c22f09e05cbac087c117751bd3e78c`，应用仓库中的补丁，在 `.local/tickerq-f14/feed` 生成三个 `10.1.1-dameng.1` NuGet 包。源码与上游许可证随固定提交取得，包内保留许可文件，源生成器取自官方 10.1.1 包。生产构建将这三个包连同适配包放到同一受控源，或者从本仓库生成后显式加入本地 NuGet 源。应用不需要相邻项目的工作树或预存 DLL。相同版本的包应保持不可变，维护补丁时提升版本。

原版 `TickerQF14=false` 只用于创建旧数据和重现基线问题，该模式不能打包。[上游 10.1.1 许可](https://github.com/Arcenox-co/TickerQ/blob/b2684f41a1c22f09e05cbac087c117751bd3e78c/LICENSE)

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

定义 `[TickerFunction]` 的项目直接引用 `W.TickerQ.Core` 10.1.1-dameng.1，以启用包内的源生成器。应用在 `UseTickerQ()` 前完成迁移。每个同时运行的副本使用不同的节点 ID，同一个副本重启时复用原 ID，以便启动时先清理该节点遗留的领取，再运行调度服务。注册方法包含 `AddTickerQ` 和 `AddOperationalStore`。应用可通过可选回调继续配置 TickerQ 的调度、函数种子等原生选项。

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

测试项目提供初始迁移和 `FencedTimeTickerClaims` 迁移。现有达梦 10.1.1 应用先停止该 schema 上所有原版调度器和写入方，再以当前应用的 migration history 生成增量迁移。增量迁移仅向 `TimeTickers` 增加带默认值的 `ClaimToken CHAR(36) NOT NULL` 和 `ClaimStarted BIT NOT NULL`，旧行以空 GUID 和 false 表示尚未使用新协议，原表、业务数据、关系和周期唯一索引保留。确认迁移及三个补丁包一致后启动新节点。原版和补丁版不可同时写同一 schema。首轮验收覆盖标准实体和专用 context。自定义实体、共享应用 context、其他 provider 数据导入仍需单独验证。

## 映射约定

- `RetryIntervals` 使用 CLOB 存储 JSON 整数数组。SQL NULL 与空数组 `[]` 分开保存。EF 使用序列比较和数组快照识别原位元素修改。
- `Request` 使用 BLOB。时间任务和周期定义各存自己的请求，周期实例仍通过原生 provider 读取周期定义的请求。
- `ExceptionMessage`、`SkippedReason` 使用 NCLOB。调度器生成的异常 JSON 保留原样。
- 时间字段使用 `TIMESTAMP(7)`，保留 100 ns 的 .NET tick。时间任务抢占使用随机 `ClaimToken` 和原子状态条件。时间仍用于调度和超时资格。
- 周期定义的 `Function` 和 `Expression` 分别映射为 `NVARCHAR2(256)` 和 `NVARCHAR2(512)`，供原生索引使用。时间任务和周期实例的 `LockHolder` 使用 `NVARCHAR2(256)`，供节点查找与抢占条件使用。

周期实例创建继承 FlexLabs runner 的事务表锁。写入同一张周期实例表时，即使业务键不同，也可能等待锁。[FlexLabs 适配说明](flexlabs.md)

## 领取与执行边界

- 等值与回退时钟下，旧快照无法覆盖新签发的令牌。过期的 `Queued` 任务仍可由 fallback 接管。正常轮询、fallback 和立即执行均返回该次实际写入的 owner、时间和令牌。待执行的 Queued 任务仍可通过 manager 改期，改期会轮换令牌；并发开始或已开始的任务禁止通过 manager 编辑。
- 执行入口以令牌原子登记一次，旧队列条目不会进入处理器。每次尝试前检查并刷新当前领取时间。删除后复用任务 ID 时，新行也获得独立的初始令牌。释放只接受当前未开始的领取，完成及重试写回只接受当前已开始的领取。同节点启动恢复会使旧令牌失效。
- 原生父子执行及跳过路径传递根领取令牌。子任务开始、刷新、写回和跳过先在同一事务锁定并验证根领取，再更改子行。覆盖原生持久化投影的两层后代。根已终止后崩溃的后代恢复沿用 10.1.1 的边界，当前没有跨进程的链续跑验收。
- `InProgress` 时间任务仍由同节点启动恢复，补丁没有增加跨节点定期回收运行中任务。启动恢复可以使仍在运行的旧处理器失去写回权。已经发生的外部副作用无法撤销，业务仍需幂等。
- 周期实例保留原生创建、执行和恢复路径。领取令牌补丁针对时间任务，周期实例的唯一写入由数据库索引和 Upsert 保证。

## 候选版本

官方 10.4.0 仍采用原版时间戳条件，已核对实际发布 DLL。10.5.1 的发布包元数据指向上游提交 `f366cf7`，该提交在领取时签发令牌，续租和完成时核对令牌，但常规和 fallback 的领取更新仍分别以时间戳等值及小于等于为条件。新增事务和令牌协议不能单凭源码确认这两条达梦回归通过。10.5.1 使用源代码可见商业条款，Community 与 Evaluation 也有各自的授权条件。当前未确认项目拥有可用于验证和交付的许可凭证，该版也涉及运行分区与存储结构迁移。因此实际达梦验证采用仍按 MIT 或 Apache-2.0 发布的 10.1.1 源码补丁。[10.4.0 NuGet](https://www.nuget.org/packages/TickerQ.EntityFrameworkCore/10.4.0) · [10.5.1 许可](https://www.nuget.org/packages/TickerQ.EntityFrameworkCore/10.5.1/License) · [发布包对应的 provider 源码](https://github.com/Arcenox-co/TickerQ/blob/f366cf7fa32575be12e3638546d04ed6d0708c63/src/TickerQ.EntityFrameworkCore/Infrastructure/BasePersistenceProvider.cs) · [领取和续租协议](https://tickerq.net/docs/5.x/guides/reliability#lease-fencing) · [存储升级说明](https://tickerq.net/docs/5.x/guides/storage-upgrade)

## 实库验证

```sh
scripts/tickerq/build-f14.sh
scripts/tickerq/run.sh provision-f14 -c Release
scripts/tickerq/run.sh seed-f14 -c Release
scripts/tickerq/run.sh verify-f14-migration -c Release
scripts/tickerq/run.sh test -c Release
```

按顺序执行迁移试验。seed 以原版 10.1.1 manager/provider 创建数据并读回，verify 在原记录上应用增量迁移，检查旧任务、周期定义、已失败的周期实例、原锁、请求和异常。完整测试会重置专用夹具表。legacy 夹具与补丁迁移夹具使用各自的用户、schema、随机密码和专用表空间，受保护的归属文件仅保存在忽略目录，权限为 `0600`。本地测试连接按需配置 `PlaintextAllowed`。

Release 套件覆盖同进程独立 host 的两节点竞争、旧持有者的释放/续期/完成、真实 TickerQ 处理器的入口、任务链、同节点重启、周期唯一竞争、长载荷和失败回读。测试进程还比较三个实际加载的 DLL 与本次 NuGet 包内 DLL 的 SHA-256。跨机器进程终止、Redis、Dashboard 和混版本运行不属于当前验收范围。

最终 Release 播种与迁移各为 **1 通过、0 失败、0 跳过**。默认补丁全套为 **14 通过、0 失败、0 跳过**，含 13 项实库测试和 1 项运行时程序集哈希核对。首轮补丁全套为 10 通过、1 失败、0 跳过，定向诊断复现了可空并发字段触发的 WDriver 绑定错误。改用非空默认值后，两轮完整回归均为 14 通过、0 失败、0 跳过。独立包消费者可构建并运行注册入口；加入原版 TickerQ 包后，构建目标按预期拒绝混装。构建脚本在备用源含旧同版本 Utilities 的情况下仍恢复本轮刚生成的 Utilities，Core 和 EF 构建均完成包内容对比。
