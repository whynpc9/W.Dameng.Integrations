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
- 可用 `core.UseDamengStores<AuthDbContext>().DisableBulkOperations()` 选择跟踪式 revoke 和 prune。跟踪式 revoke 在一个 `Serializable` 事务中提交，跟踪式 prune 每批一个 `Serializable` 事务。WDriver 当前拒绝 `RepeatableRead`，适配器显式请求 `Serializable`。事务启动失败立即报错。批量操作出错时向调用方传播错误，避免对结果未知的命令自动重放。

跟踪式维护操作要求作用域的上下文没有待保存修改，且可以启动独立事务。应在专用 DI 作用域中运行撤销和清理，并在新作用域读回其结果。批量 DML 绕过 EF 跟踪，旧作用域中的已跟踪实例仍持有旧快照。注册适配时关闭 OpenIddict 实体缓存，以免维护操作完成后新作用域仍读取已失效的缓存项。收到事务提交状态未知的错误时，应先用独立连接核对数据，再决定是否重试。

OpenIddict 6.3.0 的原生 revoke 引用 EF Core 10 已移除的 `SetPropertyCalls<T>`，实库调用复现了 `TypeLoadException`。原生删除和跟踪式 prune 的事务辅助方法会捕获事务启动异常并返回空事务。测试注入了启动失败并使用过期 Application 触发并发冲突，独立读回确认原生批量删除已删掉 Token、Application 仍在。适配后的启动失败路径保留两类数据。

## 实库入口

```sh
OPENIDDICT_DAMENG_TEST_TRANSPORT=PlaintextAllowed scripts/openiddict/run.sh provision
scripts/openiddict/run.sh test
```

`provision` 在代码内读取约定的 admin 配置文件，可传入文件路径覆盖默认值。该入口使用官方驱动连接管理员并执行建用户和建表空间命令，所有功能测试均使用 WDriver。首次运行生成独立用户、默认表空间和随机密码，并用 WDriver 校验登录身份与默认表空间。凭据和所有权记录保存在 Git 忽略的 `.local/openiddict/schema.json`，权限为 0600。重复执行会验证已创建的隔离环境，遇到未完成记录则停止并保留状态供检查。测试会清理此隔离 schema 内四张 OpenIddict 表中的数据。默认传输设置为 `RequireTls`，示例中的明文参数仅供本地实库测试使用。

验收范围以服务器自报 Dameng 8.1.5.60、UTF-8 明文环境为限。自定义实体或主键、其他驱动和服务端版本、TLS 传输、现有数据库的迁移升级、OAuth/OIDC HTTP 流程及提交应答丢失未做验收。

2026-11-07 Release 实库验收共 10 项，10 项通过、0 项失败、0 项跳过。覆盖四类实体 CRUD、URI 匹配、长 payload 和 JSON、七个撤销入口、过期快照并发冲突、两类级联删除、跨 1,000 行批量 prune、跟踪式 prune、事务启动失败及写入后回滚重试。提交和回滚后的状态由禁用连接池的新物理连接独立读回。
