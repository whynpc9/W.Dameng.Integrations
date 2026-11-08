# 不连接数据库的包验证

F01 的可重复入口是 `scripts/verify-packages.sh`，需要 Python 3.9+ 和仓库 `global.json` 指定的 .NET SDK。命令不会读取数据库密钥、执行 provisioner、启动消费者或运行实库测试。

```sh
scripts/verify-packages.sh --negative-checks
```

默认命令执行以下阶段，任何失败返回非零退出码：

1. Release 构建根解决方案，随后分别编译六个 provisioner。
2. 将四个运行时项目打包到本次独有的新目录，检查包中 README 以及 DLL 与 Release 输出的逐字节一致性。
3. 在仓库外的临时目录生成全库消费者和 Hangfire 单包消费者。只有本次包的精确 PackageReference，没有 ProjectReference，不导入仓库的 Directory.Build.props/targets；SDK 与源构建一致。
4. 精确包源映射限定四个适配包只能来自本次产物目录。任务缓存仅复用源运行时项目解析到的外部依赖，不复制测试依赖图或旧适配包；不修改或清空全局缓存。
5. 核对实际消费的适配包版本、缓存 nupkg 哈希、复制到消费者输出的 DLL，以及关键 EF、驱动、第三方包版本。Newtonsoft.Json 必须为至少 13.0.3 的稳定 13.x。Hangfire 单包消费不得混入 EF 或测试框架依赖。

`--negative-checks` 额外执行两个真实的失败恢复检查：使用没有适配包的错误包源，必须得到对应适配包的 NU1101/NU1102；显式请求 Newtonsoft.Json 11.0.1，必须因与 13.0.3 下限冲突得到 NU1605。只有预期错误成立才记录该反例通过，普通网络失败不作为反例通过证据。两个正向消费者先通过，反例后执行。

日志、打包产物、消费者的项目/源代码/资产文件及 `report.json` 保存到 `artifacts/package-validation/<run-id>/`。可用 `--output <新目录>` 指定位置；拒绝复用已有目录，以免误验旧产物。临时消费者的构建目录结束后删除，证据副本用于检查而非直接在仓库内构建。

这是包依赖与编译验证，不是数据库兼容性结果。实库测试仍按各库文档单独准备隔离 user/schema 和表空间，在确认资源独占后运行。将来有意升级 EF/驱动/第三方版本时，同步更新脚本中的版本契约并重新验证，不能把现有版本合同静默放宽。
