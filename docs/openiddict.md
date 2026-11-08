# OpenIddict 6.3 达梦存储

`W.OpenIddict.Dameng` 面向 .NET 10 和 OpenIddict.EntityFrameworkCore 6.3.0 的默认字符串主键模型。实库基线是 EF Core 10.0.12、W.EntityFrameworkCore.Dameng 0.5.0、W.EntityFrameworkCore.Dameng.WDriver 0.5.0-preview.1、W.DamengProvider 0.3.1。WDriver 发布包精确依赖上述基础提供程序和 W 驱动，EF Core 的依赖范围为 `[10.0.12, 11.0.0)`。实库验收使用 10.0.12。UniWeb 的集中版本配置同样使用 OpenIddict 6.3.0。

## 接入

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.EntityFrameworkCore;
using W.OpenIddict.Dameng;

services.AddDbContext<AuthDbContext>(options =>
    options.UseDamengWithWDriver(connectionString));

services.AddOpenIddict().AddCore(core =>
    core.UseDamengStores<AuthDbContext>());

public sealed class AuthDbContext(DbContextOptions<AuthDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.UseOpenIddict();
}
```

使用 UniWeb 的 `AddUniWebOpenIddict` 时，可在现有 `configureCoreOptions` 回调中调用 `core.UseDamengStores<TDbContext>()`。

原生 OpenIddict 模型、Scope Store、四种实体的查询和创建更新逻辑继续使用上游实现。建表使用原生 `UseOpenIddict()` 模型，保留原始长度、唯一索引、复合索引和外键。无长度限制的 JSON、URI 集合和 payload 由提供程序映射到 NCLOB。带引用 ID 的 token 继续由原生 manager 对引用 ID 做转换和查询。

## 写入行为

- Application 和 Authorization 删除在同一个显式 `Serializable` 事务内按外键顺序处理 Token、从属 Authorization 和根实体。每页最多 16 行，只投影 ID 和并发戳；通过 Application 直接关联或 Authorization 间接关联的 Token 均纳入。已跟踪实体保留原始并发戳，其他行用投影值构造轻量删除实体。每页写完后分离该页实体，根实体成功删除后统一提交。根或从属实体的并发冲突会回滚整图并报告 OpenIddict `ConcurrencyException`。原始戳为 NULL 时，在同一事务内按 ID 和 `IS NULL` 执行条件删除并要求恰好影响一行，绕过已发布 WDriver 栈的跟踪删除绑定问题。
- 授权和令牌的批量撤销使用 EF Core 10 的 `ExecuteUpdateAsync`。撤销同时更新并发戳，使已有的过期快照无法覆盖撤销状态。
- 批量 prune 按原有候选条件和主键顺序，每批最多 1,000 行、每次调用最多 1,000 批。每批由数据库执行一条删除语句。已完成的批次独立提交，后续失败会向调用方抛出。
- 可用 `core.UseDamengStores<AuthDbContext>().DisableBulkOperations()` 选择跟踪式 revoke 和 prune。跟踪式 revoke 在一个 `Serializable` 事务中提交，跟踪式 prune 每批一个 `Serializable` 事务。WDriver 当前拒绝 `RepeatableRead`，适配器显式请求 `Serializable`。事务启动失败立即报错。跟踪式维护在 DML 后、调用 Commit 前再次检查取消令牌，已取消的本地事务会回滚。批量操作出错时向调用方传播错误，避免对结果未知的命令自动重放。

跟踪式维护操作要求作用域的上下文没有待保存修改，且可以启动独立事务。应在专用 DI 作用域中运行撤销和清理，并在新作用域读回其结果。批量 DML 绕过 EF 跟踪，旧作用域中的已跟踪实例仍持有旧快照。注册适配时关闭 OpenIddict 实体缓存，以免维护操作完成后新作用域仍读取已失效的缓存项。提交请求发出后若结果未知，应先用独立连接核对数据，再决定是否重试。

OpenIddict 6.3.0 的原生 revoke 引用 EF Core 10 已移除的 `SetPropertyCalls<T>`，实库调用复现了 `TypeLoadException`。原生删除和跟踪式 prune 的事务辅助方法会捕获事务启动异常并返回空事务。测试注入了启动失败并使用过期 Application 触发并发冲突，独立读回确认原生批量删除已删掉 Token、Application 仍在。适配后的启动失败路径保留两类数据。

## 实库入口

```sh
OPENIDDICT_DAMENG_TEST_TRANSPORT=PlaintextAllowed scripts/openiddict/run.sh provision
scripts/openiddict/run.sh test
```

`provision` 在代码内读取约定的 admin 配置文件，可传入文件路径覆盖默认值。该入口使用官方驱动连接管理员并执行建用户和建表空间命令，所有功能测试均使用 WDriver。首次运行生成独立用户、默认表空间和随机密码。初始化建表和每次清表前，夹具在执行操作的同一连接上实时校验登录用户、当前 schema 和默认表空间。凭据和所有权记录保存在 Git 忽略的 `.local/openiddict/schema.json`，权限为 0600。重复执行会验证已创建的隔离环境，遇到未完成记录则停止并保留状态供检查。测试会清理此隔离 schema 内四张 OpenIddict 表中的数据。默认传输设置为 `RequireTls`，示例中的明文参数仅供本地实库测试使用。

验收范围以服务器自报 Dameng 8.1.5.60、UTF-8 明文环境为限。自定义实体或主键、其他驱动和服务端版本、TLS 传输、现有数据库的迁移升级、OAuth/OIDC HTTP 流程及提交应答丢失未做验收。

2026-11-07 Release 实库验收共 10 项，10 项通过、0 项失败、0 项跳过。覆盖四类实体 CRUD、URI 匹配、长 payload 和 JSON、七个撤销入口、过期快照并发冲突、两类级联删除、跨 1,000 行批量 prune、跟踪式 prune、事务启动失败及写入后回滚重试。提交和回滚后的状态由禁用连接池的新物理连接独立读回。


## 图删除验收

`scripts/openiddict/run.sh test --filter FullyQualifiedName~GraphDeletion --logger 'console;verbosity=detailed'` 可重跑分页删除用例。删除测试覆盖直接、间接和跨应用关联，以及整图之外应保留的行。故障测试覆盖两种根的首、中、末页陈旧 Token、Application 的后续 Authorization 页陈旧并发戳、两种根在全部从属页写完后的并发冲突、NULL 原始戳，以及写完两页后取消或抛错。测试用新物理连接逐行比较回滚后的关系、并发戳和 Token 内容，并验证同作用域的后续 SaveChanges 写入零行、新作用域重试成功。

每次查询按数据库 ID 顺序读取事务内尚未删除的首页。已删除页从同一事务的后续查询中消失，因此无需偏移量或客户端字符串排序。分页限制的是本次删除新增的跟踪实体，调用方此前加载的实体仍可能占用内存。自定义 SaveChanges 拦截器应预期一次删除调用可能包含多次 SaveChanges，以及新跟踪的删除实体只包含 ID 和并发戳。NULL 戳行走 ExecuteDelete，绕过 SaveChanges 拦截器；全图均为 NULL 戳时，删除可以完全通过 ExecuteDelete 完成。

2026-11-08 Release 实库复验执行 17 项，17 项通过、0 项失败、0 项跳过。新增覆盖包括同一用户的备用 schema 拦截、两类级联中的陈旧从属 token、预取消和写入后提交前取消，以及大图读回和回滚重试。提交请求已发送但结果未知的情况仍属独立场景。

本轮 Release 全量实库执行 30 项，全部通过。随后定向执行 6 项，全部通过，其中新增两种根的陈旧 NULL 戳用例，其余四项覆盖更新后的探针和 NULL 从属戳。最终覆盖 32 个不同用例，两轮均为 0 失败、0 跳过。早期定向分别通过 7 项和 2 项；首次全量在报告 1 项通过、3 项失败后中止，定位到 NULL 戳的跟踪删除绑定，改用显式条件删除后先定向通过 2 项，再进行上述复验。

实测每个图包含 35 个目标 Token，每行含约 32 KiB ASCII payload 与 16 KiB ASCII JSON 字符串。Application 图还跨两页删除 17 条从属 Authorization，并在前两页 Token 写完后注入故障，独立确认图行数和样本 payload/properties，随后以新作用域重试。

| 根实体 | Token 页 | 候选查询 | 峰值跟踪从属实体 | 新物化 Token LOB 内容（UTF-16 字节） | 进程级托管分配（字节） | 删除耗时（ms） |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Application（故障后重试） | 3 | 7 | 16 | 0 | 12,909,824 | 27,728 |
| Authorization | 3 | 4 | 16 | 0 | 8,656,088 | 18,818 |

探针在取得根实体后计时，测到成功提交为止。候选查询计数包含最后返回空页的查询。实际数据读取器的返回列均为 ID 和并发戳。分配量是进程级累计差值，不是堆峰值、UTF-16 内容大小或网络传输量。耗时来自各一次样本，未据此计算加速比。分页仍持有一个覆盖整图的事务，整图的锁占用和事务持续时间不受页大小限制。
