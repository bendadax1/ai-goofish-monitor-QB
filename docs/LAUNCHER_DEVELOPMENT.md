# Launcher 无打包开发与诊断

这是 Launcher 重构后的无打包开发入口，不是新发行包。统一启动用例、独立准备步骤与 UI 接线已落地；保留原有发行校验和所有数据保护。源码回归与成品放行分开记录。

## 只编译，不测试、不发布

在仓库根目录运行：

```powershell
./scripts/portable/build-launcher-prototype.ps1 -BuildOnly -Incremental -DevelopmentBuild
```

复用锁定 SDK/NuGet 缓存，产物固定在 `.tmp/build/launcher-refactor/development/`，使用排他构建锁，连续修改无需复制运行时或生成 ZIP。未开启 `-DevelopmentBuild` 时使用独立的 `full` 目录，不混用开发与发行编译结果。

## 编译并测试，不发布

```powershell
./scripts/portable/build-launcher-prototype.ps1 -NoPublish -Incremental -DevelopmentBuild
```

运行 Core（含共享 Runtime 策略）、HTTP 契约、Python 生命周期、Windows 进程、离屏 UI、真实按钮的端口 UI 和启动诊断测试。真实 PG/Python 栈仍须显式 `-PythonIntegration`；本命令默认不包含完整包验收、原生桌面或真实业务任务。

### 一条命令验证真实启动链路，不重新打包

```powershell
./scripts/portable/build-launcher-prototype.ps1 -Incremental -RuntimeAcceptance `
  -BundleRoot ./launcher/dist/portable-acceptance-frozen-20260927-p1-r9
```

`-RuntimeAcceptance` 强制禁止发布，增量编译后串行运行默认套件、真实 PG、无界面 Host、真实 PG/Web + 离屏 UI、旧进程接管测试。它复用已校验候选中的只读组件，在独立合成实例上验证首启、重启、端口冲突、取消回滚和重开；失败夹具保留。不得与 `-BuildOnly` 或 `-CoreOnly` 同用。

这条命令不等于最终 ZIP、原生桌面、干净 Windows、备份恢复或 Docker 全部放行。备份恢复属于单独的显式验收，记录见执行文档。

不带这些新选项的原构建/发布路线保持不变。`-BuildOnly` 与集成测试开关冲突时明确拒绝，避免把未执行测试误报成功。`-DevelopmentBuild` 禁止 publish，发行入口不识别 `--development`。

## 启动新编译的 Launcher

```powershell
./scripts/portable/start-launcher-development.ps1 `
  -BundleRoot ./launcher/dist/portable-acceptance-frozen-20260927-p1-r9 `
  -SessionId manual-refactor
```

可加 `-NoBuild` 复用刚编译的代码。窗口明确显示“开发隔离模式”，默认仍由用户手动点击启动。

- 运行新编译的 C# App/Core/Windows，不替换旧候选文件。
- 先校验所选完整候选的 manifest，复用其只读 Python/PG/Chromium及后端程序。此入口用于 Launcher C# 迭代；修改 Python 源码不会自动注入旧候选。
- 数据和 Launcher 偏好只放在 `.tmp/tests/launcher-development/<SessionId>/`，不能通过参数指向任意真实数据目录。
- 相同会话标识用于停止后重开；缺少开发标记的已有目录、不同 release ID、路径穿越和重解析点拒绝使用。
- 不自动抓取、调用付费 AI 或发送通知；不要向合成开发实例导入真实业务备份或登录态。
- 不会在发行包校验失败后自动降级到开发模式。

## 启动诊断

Launcher 入口写入 `%LOCALAPPDATA%/AiGoofish/Launcher/diagnostics/`；不可用时尝试 `%TEMP%/AiGoofish/Launcher/diagnostics/`。两者都不可写时保留有界内存事件，UI 日志显示存储失败，服务生命周期不因日志写入失败而被中断。

每次进程有独立会话 ID，UTF-8 无 BOM JSONL，每段最多 256 KiB、最多 4 段；不覆盖或自动删除旧会话。达到本会话上限后停止落盘并报告容量事件，内存保留最近 128 条。这里是单会话限制，不是历史目录总容量自动清理。

记录阶段、UTC 时间、进程内耗时、操作 ID、受控异常类别、HResult/系统错误码、进程退出码和输出特征类别。进程输出只提取地址占用、权限、缺模块、认证、连接、设置口令等固定类别；原始输出和异常原文不写入诊断。未识别的错误仍可能只有类别，需要后续增加安全分类，不能宣称所有错误均可精确定位。

“打开日志目录”现在优先打开 Launcher 启动诊断目录；后端业务日志仍在实例目录，两个来源不混淆。即使发行包预检失败、Host 未创建，也能预览并导出诊断。导出只含受控事件，不包含磁盘路径、配置、凭据和业务数据；保存内容与用户确认的预览保持一致。

## 当前仍未完成

- 干净 Windows、完整业务及新版 beta 制品验收；r10 本机原生发行窗口的启动与诊断验收已完成，范围见 [beta 版本说明](RELEASE_1.1.0.0-beta.md)。
- 无法核验的历史半初始化数据不会自动修复或迁移；保留数据并走诊断/恢复流程。

## 统一启动用例（连续收尾）

`LauncherRuntime` 位于 Core，不引用 Avalonia 或 Windows。它统一决定：有效端口可用则启动；确认为绑定冲突且开启自动模式、没有待应用候选时自动避让；固定模式或有手动候选时返回明确拒绝，不偷偷应用候选，也不因任意启动失败换端口。

Windows Host 在实例维护锁和同一个 Coordinator 维护会话内提供启动能力；读取配置、核验 intent、端口探测、启动、进程身份及 Ready 核验不再跨 UI 往返。自动切换复用已有 revision/intent/回滚逻辑，普通启动失败仍按归属安全收尾。取消自动启动要先确认回滚；无法确认时保留 intent 和 lease。

UI 按钮、Host 的兼容 `StartAsync`、无界面包验收、进程接管种子和备份恢复验收现在调用同一个启动入口。UI 只呈现结果、失效旧业务会话/诊断预览并打开管理页；不再持有独立端口探测器或启动前检算法。正常启动仍保留安全取消按钮，手动端口事务维持原有忙碌保护。

以下按批次保留的叙述说明历史阶段；其中“尚待统一 Runtime”的旧状态已由本节取代。

## 分配与初始化边界（第二批）

新建 Host 在启动数据库前持久化 `data/launcher/initialization.json`，使用 `Allocated` 表示完成元数据分配、数据库尚未初始化。关闭后重开会保留实例 ID 和凭据，等待手动首启，不触发“已初始化实例自动启动”。普通启动与重启均调用 Host 的 `StartAsync`，初始化检查也覆盖端口切换与回滚使用的 Coordinator。

在数据库初始化步骤前原子提交 `InitializationStarted`，失败不会退回 `Allocated`。该文件只控制首次初始化许可；数据库可用性仍以原有集群、provision 和进程身份记录为准，不把这个字段当作 Ready。已有完整实例没有这个新文件也继续走原有恢复核验。

只有持有实例锁、匹配实例 ID，且目录中没有 PGDATA、初始化/进程/业务痕迹的 `Allocated` 实例可继续。真实初始化中断、身份不匹配、未知状态、缺少新记录的旧半成品仍拒绝自动重试；旧半成品不自动迁移或补造标记。分配记录提交前的崩溃或不完整元数据仍保守拒绝，不宣称覆盖任意断电点。

第二批没有把 provision 从长期组件中拆出；后续仍须统一运行时与 UI 接线。不得删除数据目录或保护标记绕过检查。分阶段记录见 `LAUNCHER_REFACTOR_EXECUTION.md`。

## 稳定运行实例与端口事务（第三批）

端口切换现在由 Host 发起、Coordinator 的维护会话执行，全程持有同一生命周期操作锁。仅在全部组件确认停止后替换 Python 组件；候选启动、Ready 核验、提交或回滚不再重建 Coordinator，也不再要求平台层回调 UI 摘挂订阅。

UI 保留原有订阅和运行日志，切换时失效旧业务会话及诊断预览，事务结束后刷新端口和快照。并发启动/停止被拒绝，关闭等待事务收尾；回滚或界面刷新无法确认时继续保持保护状态。已有 intent、revision、实例身份及数据保护不变。

源码离屏测试与复用 r9 的真实 PG/Web 验收已覆盖成功切换、占用端口回滚、Coordinator 身份不变、原日志保留及订阅延续。尚未完成完整 `LauncherRuntime`，也不能用这些结果替代新发行包或原生桌面验收。

## 准备步骤不再伪装成服务（第四批）

真实 Host 的长期组件只有 PostgreSQL 和 Python Web。业务数据库准备由独立的 `WindowsPortableProvisionStep` 实现，在 PG 启动后、Web 启动前执行，使用同一个 Coordinator 操作锁；UI 启动、端口候选启动和回滚复用此序列。

快照把 `Components` 与 `StartupSteps` 分开，UI 和脱敏诊断分别显示“准备中／已完成／准备失败”，不再把 provision 显示为长期运行，也不在停止服务时撤销完成状态。完成记录仍由原有文件与实例身份核验；重启仅核验，不重做已完成 provision。新 Host 的步骤在核验前显示“待核验”，不凭文件名假定已完成。

步骤状态不等于进程安全状态：辅助进程仍在运行、归属未知或观察失败时，保留数据库依赖和实例锁，禁用端口操作、恢复入口与新启动。取消等待不可中断步骤结束；接管只核验完成记录，不能执行 provision。已有初始化中断拒绝规则保持不变。

第四批源码、离屏 UI、真实 PG/Web 以及旧 Launcher 退出后的进程接管验收通过，开发缓存已更新。完整 `LauncherRuntime` 尚待落地；未生成新包，未完成原生窗口或干净 Windows 放行。

## initdb 与服务启动分开（第五批）

产品编排序列为：`postgres-initialize` 步骤 → PostgreSQL 服务 → `database-provision` 步骤 → Web 服务。已移除初始化/PG 启动的组合包装类；底层 initdb 命令与集群校验仍由 Windows PG 适配器实现，未重写其磁盘格式或初始化策略。

initdb 完成后、PG 尚未启动时可以到达取消安全点。只有本次控制器亲自完成 initdb、尚未尝试启动主进程、完成记录一致且没有 postmaster PID 时，才确认此全新集群没有运行主进程；重开不继承这个内存证明。该状态不代表 provision 完成，不因此获得对半初始化实例自动重试的许可。

初始化步骤独立记录辅助进程安全状态；未确认 initdb 退出、发现旧中断记录或无法读取状态时，不能启动主进程或宣称能够释放实例锁。进程接管只被动核验完成记录，不调用 initdb。诊断导出新增白名单 `postgres-initialize` 步骤。

统一应用服务仍未完成：UI 目前还决定自动端口避让、固定端口前检和 Ready 后呈现。下一批应将这些启动决策收进不依赖 Avalonia 的 `LauncherRuntime` 用例，并复用 Host 的事务锁、revision 和 Ready 校验；不能仅新增转发层或复制另一套启动逻辑。
