# FlexLabs Upsert 达梦适配

项目为 `src/W.FlexLabs.Upsert.Dameng/W.FlexLabs.Upsert.Dameng.csproj`，目标框架为 .NET 10。运行时使用 FlexLabs 10.1.0、EF Core 10.0.12、W.EntityFrameworkCore.Dameng.WDriver 0.5.0-preview.1（依赖 W.EntityFrameworkCore.Dameng 0.5.0 和 W.DamengProvider 0.3.1）。TickerQ.EntityFrameworkCore 10.1.1 声明的 FlexLabs 范围为 `[10.0.0-beta1, 11.0.0)`。

```csharp
using Microsoft.EntityFrameworkCore;
using W.FlexLabs.Upsert.Dameng;

options.UseDamengWithWDriver(connectionString).UseDamengUpsert();
```

接入使用 FlexLabs 的 `ReplaceUpsertCommandRunner<DamengUpsertCommandRunner>()`。注册应和 WDriver 配置放在同一个 DbContext 的 options 构建中。

## 执行语义

- `Run()` 和 `RunAsync()` 返回达梦报告的受影响行数。匹配且 `NoUpdate()` 时返回 0，插入或执行匹配更新时计入实际行数。支持单列及复合匹配，匹配列为 NULL 时按 NULL 等于 NULL 处理。调用方应保证这些匹配列按此规则至多匹配一行。
- `WhenMatched` 和 `UpdateIf` 的表达式会按 EF 列映射区分字符串拼接和数值加法。两个字符串使用 `||`，NULL 操作数回退为空串，CLOB 拼接使用 CLOB 类型回退以避免收窄。支持未使用值转换器的字符串列与字符串常量。依赖非字符串对象转换或自定义值转换器的拼接会报错。服务器兼容模式仍决定空串的存储语义。[达梦字符串函数文档](https://eco.dameng.com/document/dm/zh-cn/pm/function.html)
- 使用 WDriver 参数与 EF 类型映射。批量每行执行一条 MERGE，同一批重复键按输入顺序处理。数据库拒绝的语句会保留原始错误。自行管理的事务失败时整批回滚，已有 ReadCommitted EF 事务时回退到保存点并由调用方决定外层事务结果。
- 采用达梦 `LOCK TABLE … IN EXCLUSIVE MODE`。同一张表的 Upsert 和其他写入都可能等待锁，与是否命中同一业务键无关。锁在事务结束时释放，外层 EF 事务会将锁持有到调用方提交或回滚。吞吐取决于表写入并发和事务时长。超时、取消及死锁由调用方处理，runner 不会重放命令。详见[达梦一致性和并发性文档](https://eco.dameng.com/document/dm/zh-cn/pm/consistency-concurrency.html)。
- 验收范围是 WDriver 的实体物理表、可映射的匹配列和 FlexLabs 支持的表达式子集。达梦 MERGE 禁止更新匹配列。视图、`RunAndReturn`、环境事务和其他隔离级别不在支持范围内。调用方事务需要保存点能力。网络故障使事务无法回退到保存点时会同时报告操作错误和回退错误。

## 本地实库验证

```sh
scripts/flexlabs/run.sh provision
scripts/flexlabs/run.sh test
```

provision 从授权的本地文件读取管理员连接，仅用官方驱动执行管理操作。它创建独立的随机用户/schema、随机密码和专用默认表空间，校验 WDriver 登录、当前 schema 和默认表空间，把所有权与连接配置保存到本 worktree 忽略的 `.local/` 文件，权限为 0600。测试只使用这份隔离配置。重复 provision 会验证已就绪的环境。创建过程中断时会保留未就绪的记录供检查。

测试使用 FlexLabs 公开 API 和独立连接读回，包含带转换的字段、Unicode、CLOB、NULL、复合键、默认值、实际行数、批量顺序、冲突后的回滚和重用、外层事务、六连接竞争、锁等待超时和取消。TickerQ 测试通过发布包 `TickerQ.EntityFrameworkCore` 10.1.1 的 `ITickerPersistenceProvider.QueueCronTickerOccurrences` 调用实际持久化路径，同一复合键只返回并持久化一个 occurrence。

实库验收共 14 项，14 通过、0 失败、0 跳过。锁等待用例确认超时或取消后未写入竞争行，原 DbContext 可继续使用。批次取消用例在首条 MERGE 成功后、第二条命令发送前触发取消，并分别验证自有事务的整批回滚，以及外层事务中先前写入的保留、后续写入和提交。

Astra High 独立审查了 runner、隔离配置和测试快照，未发现阻塞问题。审查指出了首条 MERGE 成功后批次取消的覆盖缺口，已补测两种事务路径。


夹具创建表前在忽略的 `.local/flexlabs-fixtures/` 写入权限为 0600 的精确 schema 和表名回执。服务器确认 CREATE 后，回执改为 `.created.json`。初始化失败与 Dispose 均逐张尝试清理已确认的表。DROP 成功后删除对应回执，失败时保留回执与脱敏错误。仍为 `.pending.json` 的记录表示 CREATE 结果待核实，回收前需确认对象归属。

F02/F08 修复后的完整实库回归为 24 通过、0 失败、0 跳过。新增覆盖数字形字符串、NULL、常量、嵌套条件、24,000 个中文字符的 CLOB、值转换器拒绝路径、部分建表失败和单表删除失败。六连接竞争、事务、超时、取消和 TickerQ 原生持久化用例仍通过。Astra High 审查发现的目标列转换器绕过问题已修复，经复审与实库回读确认。
