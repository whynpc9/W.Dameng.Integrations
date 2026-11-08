# 2026-11-08 审查问题处理记录

审查基线为 `d561c3a9777100f77521f42e178fa977fdde1db2`。六个原库会话继续使用 `gpt-6.1-sol` High，在各自 worktree 处理对应问题，并分别由 `gpt-6-astra` High 独立上下文子代理审查。主会话处理根包验证入口、检查实际差异及原始验收输出，并合并到 main。用户随后授权整理 README、相关文档并推送源码；本轮未发布 NuGet 包。

## 各项处理

| 问题 | 处理与状态 | 实现提交 |
| --- | --- | --- |
| F01 | 已完成：固化不访问数据库的源码、六个 provisioner、四个包及独立消费验证；包含错误包源和依赖降级反向检查 | `cd910db` |
| F02 / F08 | 已修复：按 EF 类型映射生成字符串拼接，保留数值加法；拒绝不支持的转换器表达式；部分夹具失败逐表清理并保留精确归属回执 | `804a20b`（包含 `960c094`、`85d3efa`） |
| F03 / F04 / F05 / F06 / F10 | 已修复：保留原始建库凭据并核验归属；初始过期以 createdAt 为基准；多集合单语句计数；清理器按批释放锁并限制工作预算；监控队列、父状态和时间线批量读取 | `30cf801` |
| F07 | 已修复：NamingConventions 转发测试参数，provision 拒绝额外参数；保留锁和 locked restore | `daee62a` |
| F09 | 已修复：OpenIddict 在同一操作连接校验 current schema，备用 schema 哨兵保留；Hangfire 在初始化前及原始 SQL 连接上校验实际归属，备用 schema 初始化被拒绝 | `8c37939`、`30cf801` |
| F11 | 已完成：跟踪维护在 DML 后提交前响应取消；预取消、取消回滚、陈旧从属 token 冲突、清洁 tracker 和新作用域重试实测通过 | `8c37939` |
| F12 | 已完成：原生 UserManager 的锁定计数、阈值、开关、重置、NULL 期限和非零时区期限精度由新 scope 与独立 SQL 读回 | `9a2311d` |
| F13 | 调查完成，优化未实施：32-token 图的完整载荷物化已测量，只读分页投影可行；分页删除的并发戳、外键顺序和全图回滚语义尚未验证 | `8c37939` |
| F14 | 调查完成，运行时缺陷未修复：等值/回退时钟均可让两个节点领取同一任务；新增测试刻画原生行为，未启动处理器，不能据此声称处理器已重复执行 | `8a7be64` |

F13 的每种图加载 3,146,496 字节 UTF-16 token LOB 内容，分两页读取 ID、并发戳和外键只返回 7,462 字节字段内容。这不是堆峰值或网络流量测量，投影读取与完整删除耗时不能用来推算加速比。生产图删除仍物化全部匹配实体。见 [OpenIddict 测量与限制](openiddict.md#图删除载荷测量)。

F14 的原生实现以 UpdatedAt 快照判定领取，但把节点时钟写回 UpdatedAt；时钟不前进或回退时旧快照可继续匹配。没有已通过验收的窄扩展点修复。后续版本涉及并发协议与存储迁移，不能直接替换版本后宣称解决。见 [TickerQ 时钟边界](tickerq.md#时间任务抢占的时钟边界)。

## 实库结果与隔离

以下为各库会话本轮最终完整回归，主会话核对日志或 TRX。它们是独立套件的结果，不是一次组合测试运行。

| 套件 | 通过 / 失败 / 跳过 | 本地原始证据（各自 worktree 下） |
| --- | --- | --- |
| FlexLabs | 24 / 0 / 0 | `.local/audit-final.log` |
| TickerQ | 7 / 0 / 0 | `.local/tickerq/results/f14-release-third.trx` |
| Hangfire | 完整 14 / 1 / 0；修订观察方式后定向 1 / 0 / 0 | `.local/hangfire/runs/review-20261108/acceptance-summary.txt`、`full.log`、`two-servers-retest.log` |
| OpenIddict | 17 / 0 / 0 | `.local/openiddict/results/followup-live.trx` |
| Identity | 8 / 0 / 0 | `.local/identity-f12-release.log` |
| NamingConventions | 11 / 0 / 0；另外 Release 筛选 1 / 0 / 0 | `.local/namingconventions/F07 results/naming-f07.trx` 为筛选运行证据；完整运行见库会话输出 |

本轮六套共 **82 个不同实库用例有通过记录**，包含 TickerQ 已知缺陷刻画测试；不能称为一次 82/82 全套运行。

Hangfire 全量唯一失败为双 server 用例新建独立回读连接超时。测试随后复用一条已校验的独立连接，以单条聚合 SQL 检查六个作业；双 worker、续租和执行次数断言保留。定向复验通过，运行时 DLL 前后逐字节一致（SHA-256 `c05cd405927a59a54d12eb8df466fb9f242ba48ba07cc43c13eea5a33dfc53c3`）。另有四路冷初始化/升级定向 1 项通过。Astra High 复审了观察方式变更。原始驱动异常曾包含端点，夹具已将打开连接失败脱敏；完整原始日志仅保留于忽略目录。

TickerQ 前两轮全套分别为 5/7 和 6/7，三个失败为 WDriver 超时，最终相同断言全套重跑通过。FlexLabs 调试期间发现并修复 CLOB 空值回退类型收窄；一次连接超时留下的两张夹具表按精确回执核对后清理，最终无待处理回执。不能把这些早期失败从执行历史中抹去。

六个库会话本轮均使用新分配的独立用户/schema 和独立表空间，原分配记录保留。主会话只在内存中检查归属记录：六个不同用户/schema、六个不同表空间、全部 ready、权限均为 0600，未输出凭据或连接地址。主会话的 TickerQ 集成复验使用初始验收时已接管的原分配，确认与本轮 TickerQ worktree 分配不同，没有并发操作同一套测试库。

## 集成检查

F01 先在独立 worktree 实现并经 Astra High 审查，再从 `cd910db` 创建全新 detached checkout 运行 `scripts/verify-packages.sh --negative-checks`：7 项验证器策略测试、六个 provisioner 编译、四个包生成、两个正向消费者、两个预期失败的 restore 检查均通过。原始记录位于该 checkout 的 `artifacts/package-validation/20261108T003205Z-14bb60e5/report.json`。整个入口不读取数据库凭据、不运行 provisioner、不连接数据库。

主分支合入新 FlexLabs runner 后，运行 `NUGET_PACKAGES=/Users/wanghongyi/.nuget/packages scripts/tickerq/run.sh test -c Release --no-restore --logger 'trx;LogFileName=tickerq-integrated.trx' --results-directory artifacts/remediation/tickerq`，组合实库复验 **7 通过、0 失败、0 跳过**。证据为 `artifacts/remediation/tickerq/tickerq-integrated.trx`。包含两项重复领取刻画测试，通过表示原生限制被稳定复现。

全部库合并后执行 `scripts/verify-packages.sh --negative-checks` 成功：7 项策略测试、解决方案及六个 provisioner 构建、四个包、两个独立消费者，以及错误包源/显式依赖降级两个预期失败检查均通过。主分支证据位于 `artifacts/package-validation/20261108T005825Z-f0620ed6/report.json`。没有运行数据库程序，也未清空全局缓存。

## 保留的边界

WDriver 扩展仍为 0.5.0-preview.1。FlexLabs 表锁和 Hangfire WRITER 串行写入策略保留。提交请求已经发出后结果未知的场景没有故障注入验收。HTTP 认证/登录、Identity Passkey、既有业务库迁移、长期负载与多机故障演练不在本轮范围。F13/F14 的调查结论不等于性能或运行时缺陷已经修复。

各库 worktree 和隔离数据库保留用于复验。审查会话产生的未跟踪 `plans/` 原样保留，未混入修复提交。
