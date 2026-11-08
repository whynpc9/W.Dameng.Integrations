# W.Hangfire.Dameng

适用于 .NET 10，支持版本固定为 Hangfire.Core 1.8.23 和 W.DamengProvider 0.3.1。这是 ADO.NET 存储组件，应用可直接传入同版本的 Hangfire.AspNetCore 配置。库内不需要 EF Core，也无需加载官方达梦驱动。

运行时包将 Newtonsoft.Json 的最低版本设为实库验收所用的 13.0.3，避免 Hangfire.Core 的传递依赖解析到受影响的 11.0.1。[GHSA-5crp-9r3c-p9vr](https://github.com/advisories/GHSA-5crp-9r3c-p9vr)

```csharp
using Hangfire;
using W.Hangfire.Dameng;

services.AddHangfire(configuration => configuration.UseDamengStorage(connectionString));
services.AddHangfireServer();
```

使用专用达梦账号及其默认 schema。首次构造 `DamengStorage` 时会创建保留的 `WHF_*` 对象，账号需要在本 schema 创建表、索引和序列的权限。驱动应保留默认的自动提交设置，存储会显式开启写事务。不同进程要使用同一 schema 才能共享作业和锁。该结构从 v1 表结构升级到 v2 索引。初始化和升级串行执行，已完成的 DDL 步骤可以在中断后继续。升级应安排在停止旧版 worker 的窗口内。

## 事务和故障恢复

- 初始过期时间以传入的创建时间加保留期计算，创建时间统一转为 UTC。显式调用 `ExpireJob` 时，过期时间仍以写事务的数据库 UTC 时间为基准。作业记录及参数原子写入，作业状态变更、状态历史和入队可在一个写事务内提交。Hangfire.Core 1.8.23 的默认客户端分两步执行，先保存作业记录及参数，再提交初始状态和入队。直接使用存储的 `JobStorageTransaction.CreateJob` 可以将这两步组合在一个事务内。Worker 的最终状态变更和租约确认在一个事务内提交，已有作业状态变更受 Hangfire 的分布式作业锁保护。
- 写事务只调用一次驱动的 `Commit`，存储层不捕获提交异常或重放写事务。驱动将未确认的提交报告为 `DmCommitOutcomeUnknownException`。Hangfire.Core 自身可按客户端和 worker 的策略重试。此次实库测试覆盖回滚和领取后崩溃恢复，提交已送达但应答丢失的故障注入未执行，提交结果未知时的端到端表现尚未验收。
- Worker 按队列行 ID 从订阅队列取作业。租约使用数据库 UTC 时间和每次领取独立生成的令牌。活跃 worker 定期续租，进程退出或续租失败达到超时后，其他 worker 可重新领取。租约一到期，旧持有者的确认和重新排队操作就会成为空操作，终态事务也会拒绝提交。作业副作用仍采用至少一次语义，重复执行时应由业务代码保证幂等。
- 分布式锁使用专用物理会话持有行锁。同一逻辑连接可重入，最外层释放锁，进程终止后由数据库回收会话锁。取得资源锁后，新持有者会等待已在途的写事务结束，再进入临界区。锁只保护仍持有会话的进程，调用方对外部系统的写入需要自己的幂等或 fencing 机制。
- 同一 schema 的所有存储写事务共用 `WHF_LOCK` 表的 `WRITER` 行锁。不同队列和不同作业之间也会争用，领取、续租、清理和心跳都可能等待其他写事务。多个 worker 可以并发执行作业，持久化吞吐仍受单写事务限制。高吞吐、长网络抖动、达梦集群故障切换和跨区域生产负载需要独立验收。锁表会按不同资源名累积锁行，当前未实现在线锁行回收。
- 默认轮询间隔 1 秒，队列不可见时间 5 分钟，每分钟续租一次。可以通过 `DamengStorageOptions` 调整。数据库连接超时与 SQL 执行仍由驱动管理。后台进程每批最多删除 200 个作业、200 个集合和 200 条计数记录。任一类别满批时，清理器释放 `WRITER` 后继续下一批。每轮至多八批，且只在 25 秒预算内开始下一批。清理在批次间检查停止信号，随后按配置间隔等待。
- 提供 Hangfire 1.8.23 的队列、服务器、状态列表、作业详情和时间线监控查询。队列概览在一条查询中统计可领和已领取项，并按队列行 ID 返回前五个可领作业。延续页面批量查询父作业状态，时间线批量查询各桶计数并把缺失桶填为零。分页按稳定的行 ID 排序，连续查询期间数据会随其他 worker 的提交更新。多集合计数按去重后的键在同一条有上限的 SQL 中执行。时间线统计对应本存储记录的状态进入次数。

当前支持 Hangfire.Core 的默认数据库队列和常规 worker、延迟、周期、延续作业路径。未适配其他存储的历史数据、Hangfire Pro 扩展、自定义队列提供者或事务级分布式锁可选特性。作业状态名和队列名的数据库列长度分别为 128 和 256，超出长度由数据库拒绝。作业调用、状态详情和参数值存储为 CLOB。

## 参考实现评估

- [DMStorage.Hangfire 1.0.0.6](https://www.nuget.org/packages/DMStorage.Hangfire/1.0.0.6) 发布包依赖 Hangfire.Core 1.7.26、Dapper 1.50.5 和 DM.DmProvider 8.3.1.28188 或其后续版本。[上游源码](https://github.com/findersky/Hangfire.DMStorage/tree/6f8fe8016a4cd7b5c8a9bf569eadb067b62a697d) 的仓库根许可为 MIT，含 DM 存储、队列和监控实现。其驱动和目标 Hangfire 契约与本项目支持的组合不同。
- 本地 [Hangfire.PostgreSql](https://github.com/whynpc9/Hangfire.PostgreSql/tree/edbb471071a8f233fc16f837deeb1c1099a992bf) 检查版本为 `edbb471071a8f233fc16f837deeb1c1099a992bf`，使用 LGPLv3，依赖 PostgreSQL/Npgsql。两个参考项目只用于评估。这里依据 [Hangfire.Core 1.8.23](https://github.com/HangfireIO/Hangfire/tree/v1.8.23/src/Hangfire.Core) 存储契约独立编写达梦 SQL 和驱动调用。

## 本地实库复测

```sh
scripts/hangfire/run.sh provision
scripts/hangfire/run.sh test
```

`provision` 只读取批准来源的管理员连接，为本 worktree 创建随机命名的用户和表空间，通过管理员查询核对表空间与数据文件。新用户登录会验证实际用户、当前 schema 和默认表空间，重复执行时也会做这三项检查。建库回执记录了数据文件及原始凭据。若在创建用户成功后中断，重试会先核对表空间及数据文件，然后用原始凭据登录确认身份再授权。发现归属无法确认的对象时会停止并保留回执。旧版未完成的回执若没有记录数据文件，需要先人工核对归属。连接配置和资源归属仅保存于忽略的 `.local/hangfire/`，权限为 0600。仅实库脚本为当前服务器显式允许明文传输，应用端连接配置由使用方提供。

使用同一个 `WHF_TEST_RUN` 环境变量运行 `provision` 和 `test`，可为一次新验收创建单独的分配回执。未设置时仍使用本 worktree 的默认回执。测试夹具在初始化和原始 SQL 操作前核对实际用户、当前 schema 和默认表空间。测试运行真实 Hangfire API 和 worker，使用独立 ADO.NET 连接回读已提交数据。测试会终止专用探针子进程来验证断连恢复。初始化测试会在该专用 schema 中移除一个存储索引，并把版本标记退回 v1 来模拟升级中断。请串行执行同一份测试配置。若要单独验证首次启动竞争，请在新建的隔离 schema 上先运行：

```sh
scripts/hangfire/run.sh test --filter FullyQualifiedName~ConcurrentSchemaInitializationAndResumableUpgrade
```

可在全新的命名分配上用 `WHF_TEST_INTERRUPT_AFTER_CREATE_USER=1` 运行 `provision`。该入口在建用户之后、写回执之前以 75 退出。去掉注入变量后，以同一 `WHF_TEST_RUN` 重跑可验证恢复。
