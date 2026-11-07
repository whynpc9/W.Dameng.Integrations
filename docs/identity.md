# ASP.NET Core Identity 达梦接入

本接入使用微软原生的 `IdentityDbContext`、`UserStore`、`RoleStore`、`UserManager` 和 `RoleManager`。应用配置 Dameng EF Core 的 WDriver 扩展即可使用本次验证的 Identity 持久化功能。

## 验证版本

| 组件 | 版本 |
| --- | --- |
| .NET SDK / ASP.NET Core 运行时 | 10.0.401 / 10.0.12 |
| `Microsoft.AspNetCore.Identity.EntityFrameworkCore` | 10.0.12 |
| `Microsoft.EntityFrameworkCore.Relational`、`Microsoft.EntityFrameworkCore.Design` | 10.0.12 |
| `W.EntityFrameworkCore.Dameng` | 0.5.0 |
| `W.EntityFrameworkCore.Dameng.WDriver` | 0.5.0-preview.1 |
| `W.DamengProvider` | 0.3.1 |

Identity 10.0.12 依赖 EF Core Relational 10.0.12 或更高版本。WDriver 扩展精确依赖基础包 0.5.0 和 W 驱动 0.3.1。基础包还会传递解析 `DM.DmProvider`，本次解析为 8.3.1.47463。以下配置由 `UseDamengWithWDriver` 选择 W 驱动；预览版扩展属于本接入的版本前提。UniWeb 当前引用 Identity EF 和 EF Core 10.0.1 时，接入前需对齐应用的 EF Core 和 Identity 版本。

## 应用注册

在 .NET 10 Web 项目中按上表添加显式包引用。使用 `dotnet ef` 时添加 Design 包，并将其设为 `PrivateAssets="all"`。从应用的密钥配置读取 W 驱动连接字符串。

```csharp
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var identityConnection = builder.Configuration.GetConnectionString("Identity")
    ?? throw new InvalidOperationException("Missing Identity database configuration.");

builder.Services.AddDbContext<AppIdentityContext>(
    options => options.UseDamengWithWDriver(identityConnection));
builder.Services.AddIdentityCore<IdentityUser>(options =>
    {
        options.Stores.SchemaVersion = IdentitySchemaVersions.Version2;
        options.Stores.MaxLengthForKeys = 128;
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<AppIdentityContext>();

// Register the application's authentication and endpoint services before Build().

public sealed class AppIdentityContext(DbContextOptions<AppIdentityContext> options)
    : IdentityDbContext<IdentityUser, IdentityRole, string>(options);
```

应用负责 Cookie、登录端点及其他认证服务。运行时和设计时使用相同的 Identity 模型选项，在应用上下文生成、审查并部署自己的 EF 迁移。DDL 部署账号需要目标 schema 的 DDL 权限和提供程序迁移锁所需权限。测试项目中的初始迁移可作为达梦 DDL 示例。已有生产数据库的升级需要按其实际迁移历史和模型差异单独设计。

涉及多个 Manager 写入的业务操作可以在同一 DI scope 内共享上下文并使用 `Database.BeginTransactionAsync()`，检查每个 Identity 操作的结果后提交。并发戳冲突后，从新的上下文读取最新版本再执行业务决定。WDriver 0.5.0-preview.1 的 EF 配置不支持 `EnableRetryOnFailure()`。

## 实库验收

在仓库根目录执行：

```bash
scripts/identity/provision.sh
scripts/identity/test.sh -c Release
```

`provision.sh` 通过授权的本地管理员设置创建独立表空间和具有随机密码的专用用户。脚本只读取来源文件的管理员连接，保存专用库的所有权与连接设置到忽略的 `.local/identity.json`（权限 `0600`）。已有就绪记录时重复执行会重新校验连接。中断后保留未就绪记录并停止，供人工核查已创建的对象。测试必须有就绪记录，连接与当前 schema 校验失败时直接报错。测试数据使用随机标识，重复运行会保留已有记录。明文传输许可只用于本地实库配置，应用使用 W 驱动默认的 TLS 要求。

| 场景 | 验证 |
| --- | --- |
| 迁移和存储位置 | 空 schema 上应用初始迁移，再次应用幂等；从独立连接核对历史和所有 Identity 表的表空间 |
| 用户及密码 | 原生 Store 注册，存储哈希，验证正确和错误密码；按规范化用户名、邮箱及字符串 ID 查找 |
| 角色、Claims、Login、Token | Manager 读写、角色成员查询、复合键约束、Token 覆盖；256 字符名称、128 字符外部键与多字节长值完整读回 |
| 规范化名称唯一性 | Manager 拒绝重复；独立连接绕过应用验证时由用户和角色的唯一索引拒绝写入 |
| 并发和事务 | 两个上下文交错写入时拒绝陈旧的用户与角色并发戳，新上下文重试成功；跨 Manager 写入显式回滚，独立查询确认无残留后重试 |
| 关联删除 | 从新上下文删除角色和用户，由实库级联删除关联记录并保留无关角色 |

验证范围是 Identity Schema Version 2、字符串用户和角色主键、新建迁移及当前实库的 32 KiB 页面。Version 3 的 Passkey、Cookie/OIDC 端到端登录、已有应用数据迁移、迁移降级和其他页大小或数据库配置未纳入验收。
