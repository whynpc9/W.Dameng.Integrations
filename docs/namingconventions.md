# EFCore.NamingConventions 与达梦

在现有达梦 schema 中，可以直接把 `EFCore.NamingConventions` 的扩展方法与 `UseDamengWithWDriver` 组合使用。本集成提供实库验收项目，应用沿用 EF Core 的模型和迁移 API。

| 组件 | 验收版本 |
| --- | --- |
| .NET SDK | 10.0.401 |
| `EFCore.NamingConventions` | 10.0.1 |
| `Microsoft.EntityFrameworkCore` 和 `Relational` | 10.0.12 |
| `W.EntityFrameworkCore.Dameng` | 0.5.0 |
| `W.EntityFrameworkCore.Dameng.WDriver` | 0.5.0-preview.1 |
| `W.DamengProvider` | 0.3.1 |

测试工程锁定直接和传递依赖。WDriver 扩展精确依赖表中的基础 provider 和 W 驱动。依赖图也包含基础包要求的 `DM.DmProvider 8.3.1.47463`。管理员建库脚本使用该包，实库测试的 EF 和独立读回均使用 W 驱动。

```csharp
options.UseDamengWithWDriver(connectionString, dameng =>
    dameng.MigrationsHistoryTable("Naming-MigrationHistory", existingSchema))
    .UseSnakeCaseNamingConvention();
```

- 表和列覆盖 snake_case、lowercase、camelCase、UPPERCASE 和 UPPER_SNAKE_CASE。测试使用带 schema 的带引号标识符创建表，并从另一个物理连接核对目录、表空间和写入的数据。
- snake_case 迁移覆盖约定命名的主键、备用键、外键和索引，以及显式命名的检查约束、带连字符的列和混合大小写序列。模型的 `HasDefaultSchema`、显式 `HasColumnName`、`HasSequence` 名称和配置的 history 表名保留原值。history 列使用 `migration_id` 和 `product_version`，应与已有库的实际定义核对后再启用命名约定。
- 达梦的标识符上限按字节计算。实库接受 128 字节的 ASCII 标识符，拒绝 129 字节。两个共享长前缀的列和索引在截断后仍可区分。PascalCase 转换增加的下划线可能让名称超过上限，例如测试中的 128 个字符扩成 191 字节。可用 `HasColumnName`、`ToTable` 或 `HasDatabaseName` 显式指定足够短的名称。多字节标识符的截断行为未验收，Unicode 数据往返单独验证。[达梦技术指标](https://eco.dameng.com/document/dm/zh-cn/pm/programmer-overview)
- `Database.Migrate` 和 `MigrateAsync` 可以使用显式配置 schema 的 history 表。测试覆盖新上下文重复执行、迁回 `0`，以及用两个连接迁回最新版本。
- 在当前 schema 中调用 `MigrationsHistoryTable("Naming-MigrationHistory")` 可生成此夹具的幂等脚本。测试用 `Idempotent | NoTransactions` 生成脚本，按 disql 的独占行 `/` 划分迁移块，经 WDriver 对同一库执行两次，并读回同一条 history 和业务数据。若指定 history schema，0.5.0 生成脚本的开头会把建 schema 的 DMSQL 块和 `CREATE TABLE IF NOT EXISTS` 拼成 `END;CREATE TABLE …`，该批次实测报 `-2007`。显式 schema 场景可用运行时 `Migrate`。覆盖范围为本项目夹具和现有 schema 中的迁移。

## 复跑

从仓库根目录运行以下命令。

```sh
scripts/namingconventions/run.sh provision
scripts/namingconventions/run.sh test
```

`provision` 只从仓库约定的本地设置读取管理员连接，在 DM 实例中创建一个随机命名的用户及其 schema 和专属默认表空间。它为该用户独立生成密码，将所有权和 WDriver 连接写入忽略的 `.local/namingconventions/owned.json`（权限 `0600`）。重复运行会核对目录和 WDriver 当前 schema。若首次创建中断并留下未就绪记录，先检查记录及对应的达梦对象，再处理该次创建的对象。测试和脚本不会复用源设置的测试 schema。

本地测试连接显式允许明文传输，以匹配验收实例。测试需要已就绪的隔离配置，缺少配置会失败。脚本串行化同一工作树的建库和测试；测试仅清理专属 schema 中由该夹具创建的具名对象。若进程被强制终止，确认没有残留的建库或测试进程后再移除本地 `run.lock` 目录。

本机 .NET 的 NuGet HTTPS 访问出现 TLS EOF 时，验收使用从 NuGet 下载的原始发布包作本地源，并复用现有全局缓存。测试包版本及内容哈希由 `packages.lock.json` 校验。

本次实库验收共 11 项，11 项通过，0 项失败，0 项跳过。复跑 `provision` 验证了现有隔离用户的默认表空间和 WDriver 当前 schema，目录读回也确认命名约定测试的表使用该表空间。
