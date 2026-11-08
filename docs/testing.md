# 构建、包验证与实库复跑

在仓库根目录执行。SDK 版本由 `global.json` 固定为 10.0.401。脚本设置可写的任务级 `DOTNET_CLI_HOME`、关闭首次体验与遥测，并采用单节点、禁用节点复用和共享编译的参数。不要为恢复失败清空全局 NuGet 缓存。

## 不需要数据库的检查

```sh
scripts/build.sh
scripts/verify-packages.sh --negative-checks
```

第一条只构建解决方案。第二条运行验证器策略测试、构建解决方案和六个 provisioner、生成四个 NuGet 包，并在仓库外创建仅依赖包的消费者。它不会运行 provisioner 或连接数据库。报告和产物位于 `artifacts/package-validation/<run-id>/`，详细机制见 [包验证说明](package-validation.md)。

网络受限而本机已有外部依赖缓存时，可显式设置 `NUGET_PACKAGES` 指向该缓存目录。包验证脚本默认保留用户的外部依赖缓存，并为消费检查创建独立缓存。不要用旧适配包覆盖本次产物，也不要把网络错误算成反向检查通过。

## 准备隔离资源

当前 provisioner 按 [AGENTS.md](../AGENTS.md) 约定读取本地管理员配置，不通过命令行传递密码。移植到其他机器前，应核对各入口的配置来源与服务器权限；仅编译成功不代表该机器具备建库条件。

每个库创建随机用户/schema、专属默认表空间和随机密码。回执只保存在 `.local/`，权限为 `0600`。重复 provision 会验证已有分配，不复用源配置中的默认测试 schema。六个库可使用同一达梦实例，但同一分配的测试必须串行执行。复制回执到另一个 worktree 会共享数据库；需要独占交接或新建分配。

测试会创建、重置或删除自己夹具的对象。数据库管理员源配置、应用生产库与其他会话资源不能作为测试目标。不要把 `.local/`、完整驱动异常或连接信息提交到版本库。

## 各库命令

```sh
# FlexLabs
scripts/flexlabs/run.sh provision
scripts/flexlabs/run.sh test -c Release

# TickerQ
scripts/tickerq/run.sh provision -c Release
scripts/tickerq/run.sh test -c Release

# Hangfire：同一个命名分配用于 provision 和 test
WHF_TEST_RUN=acceptance scripts/hangfire/run.sh provision
WHF_TEST_RUN=acceptance scripts/hangfire/run.sh test

# OpenIddict：明文参数仅对应本地验收环境
OPENIDDICT_DAMENG_TEST_TRANSPORT=PlaintextAllowed scripts/openiddict/run.sh provision
scripts/openiddict/run.sh test

# Identity
scripts/identity/provision.sh
scripts/identity/test.sh -c Release

# NamingConventions
scripts/namingconventions/run.sh provision
scripts/namingconventions/run.sh test -c Release
```

`PlaintextAllowed` 不应直接照搬到应用部署配置。各库的注册、迁移和支持边界见 [README 集成表](../README.md#集成范围)。Hangfire 与 OpenIddict 的 test 入口默认使用 Release；NamingConventions 的 provision 不接受额外参数。

需要保存筛选后的结果时，测试入口可转发 `dotnet test` 参数，例如：

```sh
scripts/tickerq/run.sh test -c Release \
  --filter 'FullyQualifiedName~Native_queue_accepts_both_claims' \
  --logger 'trx;LogFileName=tickerq-clock.trx' \
  --results-directory artifacts/local-tests/tickerq
```

该例是**已知缺陷刻画**：测试通过表示复现了重复领取，不表示问题已修复。只有已完成依赖恢复且源码配置一致时才使用 `--no-restore`。

## 结果与恢复

验收必须记录执行数、通过、失败、跳过、代码版本和隔离归属。实际第三方 API 写入后，以独立连接或新作用域读回持久化状态。并发测试还应明确交错顺序，不能只以 HTTP 接受、方法返回或零条测试判断成功。

连接或命令超时后保留原始轮次结果，诊断后再定向复验；如果改动了实现，需要重跑受影响路径并审查。不同轮次的通过记录不能合并描述成一次全套通过。分享日志前移除连接端点、凭据和业务数据。

建库中断时保留原归属回执，核对精确对象后按相应入口的恢复规则处理。Hangfire 支持对已记录的数据文件和原始凭据进行恢复核验；旧的不完整回执缺少数据文件信息时会停止。FlexLabs 夹具留下的 `.pending.json` 表示 CREATE 结果需要核实，`.created.json` 表示已确认创建。不得按宽泛名称前缀删除未知归属对象。

本轮执行结果、重跑历史与未解决问题见 [2026-11-08 汇总](review-remediation-2026-11-08.md)。
