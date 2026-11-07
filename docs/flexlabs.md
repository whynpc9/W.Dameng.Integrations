# FlexLabs Upsert 达梦适配

项目为 `src/W.FlexLabs.Upsert.Dameng/W.FlexLabs.Upsert.Dameng.csproj`，目标框架为 .NET 10。运行时使用 FlexLabs 10.1.0、EF Core 10.0.12、W.EntityFrameworkCore.Dameng.WDriver 0.5.0-preview.1（依赖 W.EntityFrameworkCore.Dameng 0.5.0 和 W.DamengProvider 0.3.1）。TickerQ.EntityFrameworkCore 10.1.1 声明的 FlexLabs 范围为 `[10.0.0-beta1, 11.0.0)`。

```csharp
using Microsoft.EntityFrameworkCore;
using W.FlexLabs.Upsert.Dameng;

options.UseDamengWithWDriver(connectionString).UseDamengUpsert();
```

接入使用 FlexLabs 的 `ReplaceUpsertCommandRunner<DamengUpsertCommandRunner>()`。注册应和 WDriver 配置放在同一个 DbContext 的 options 构建中。目标表使用主键或能唯一定位行的匹配列。批量按输入顺序执行 MERGE，使用一个 ReadCommitted 事务持有目标表的写锁。外层已有 EF 事务时复用该事务，失败回退至保存点。调用方负责提交外层事务，表锁随事务释放。
