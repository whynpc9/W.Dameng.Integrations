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

- Application 和 Authorization 删除会在显式 `Serializable` 事务内先跟踪并删除相应的 Token、Authorization，再删除根实体。Token 可能通过应用或授权关联命中级联，两种关联均纳入。根或从属实体的并发冲突会回滚整个删除并报告 OpenIddict `ConcurrencyException`。
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


## 图删除载荷测量

`scripts/openiddict/run.sh test --filter FullyQualifiedName~GraphDeletionMeasures --logger 'console;verbosity=detailed'` 可重跑载荷探针。每种根实体使用 32 条 token，每条包含 32 KiB 的 ASCII payload 和 16 KiB 的 ASCII JSON 字符串。探针执行两页只读投影，再通过真实 manager 删除并用独立连接核对。Application 路径还在 SaveChanges 报告写入 34 个实体后、提交前注入失败，独立确认全图行数和样本 payload 后再重试。

| 根实体 | 跟踪路径物化的 token LOB（UTF-16 字节） | 到 SaveChanges 开始（ms） | manager 删除完成（ms） | 两页投影字段（UTF-16 字节） | 两页投影读取（ms） |
| --- | ---: | ---: | ---: | ---: | ---: |
| Application（故障后重试） | 3,146,496 | 58,624 | 74,036 | 7,462 | 634 |
| Authorization | 3,146,496 | 18,583 | 33,305 | 7,462 | 687 |

字节数仅按字符串内容计算。跟踪路径只统计 token 的 Payload 和 Properties，投影统计 ID、并发戳和两个影子外键；均未测量堆峰值、总分配量或网络流量。计时来自各一次实库样本。投影计时只覆盖读取，完整删除计时还包含跟踪、DML 和提交，两类计时不能用来推算分页写入加速比。

只读投影说明 ID、并发戳和影子外键可以分两页读取。生产图删除继续使用原有跟踪路径。分页写入仍需验证已跟踪从属实体的原始并发戳、完整的关联匹配和 FK 顺序，以及全部分页写入和跟踪状态在同一个 Serializable 事务中的提交与回滚行为。只读探针没有验证这些写入契约。

2026-11-08 Release 实库复验执行 17 项，17 项通过、0 项失败、0 项跳过。新增覆盖包括同一用户的备用 schema 拦截、两类级联中的陈旧从属 token、预取消和写入后提交前取消，以及大图读回和回滚重试。提交请求已发送但结果未知的情况仍属独立场景。
