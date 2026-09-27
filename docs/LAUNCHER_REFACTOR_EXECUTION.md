# Launcher 重构执行记录

## 连续收尾（2026-09-27，统一 Runtime 与产品接线完成）

本节取代前五批中“完整 Runtime 待实施”的状态。本次收尾完成启动架构的代码与可在当前环境执行的回归，不将整个 P1 公开发行矩阵宣称完成。

- Core 新增 `LauncherRuntime` 和平台启动会话契约，统一可用端口启动、自动避让、固定冲突拒绝及 pending 不自动应用的规则。不依赖 Avalonia，也不复制 Windows 进程/磁盘实现。
- Host 在现有实例维护锁与 Coordinator 维护会话内提供能力，端口配置/intent 校验、探测、启动、Ready/身份核验连续执行；自动切换和手动应用共用原有事务实现。正常启动的最终 Ready 核验失败会按正常归属停止；无法确认时仍保护依赖与实例锁。
- UI 删除独立启动前检和端口探测器，按钮直接调用共享 Runtime；继续失效旧业务会话/预览、刷新有效地址、显示首次设置状态。正常启动保留安全取消，不把所有启动当成禁止取消的端口维护。
- 自动候选启动取消后先确认回滚，再返回取消结果，保留原有效端口且允许重试。新增真实 PG/Web 回归覆盖该路径，不只测试内存 fake。
- Coordinator 维护前检查现在发布新鲜被动快照：固定端口冲突虽不启动，也能从 NotStarted/Unknown 显示已核验停止，端口设置保持可编辑。
- Host 兼容启动、无界面包验收、接管种子和备份恢复启动都走同一产品入口。移除旧 UI 测试专用前检路径，离屏测试直接点击同一启动用例。
- 构建脚本默认纳入端口 UI 回归，新增 `-RuntimeAcceptance -BundleRoot <已验证完整包>` 一键无发布回归，强制 NoPublish，并串行运行真实栈，不新增依赖。

### 最终验证

- 普通源码构建 0 警告/0 错误；Core（新增 8 格端口策略组合、并发/异常/取消/重试）、HTTP、Python 生命周期、Windows 进程、真实 PG 集成、离屏 UI、启动诊断和端口 UI 全部通过。
- 完整执行：`build-launcher-prototype.ps1 -Incremental -RuntimeAcceptance -BundleRoot launcher/dist/portable-acceptance-frozen-20260927-p1-r9`。结果包括 `BUNDLE_ACCEPTANCE=PASS`、`AUTO_PORT_REAL_UI=PASS`、`HOST_UI_RECOVERY=PASS`；复用 r9 冻结组件，运行新 C#，不是旧 EXE 验收。
- 诊断导出器 `LAUNCHER_DIAGNOSTIC_EXPORTER_PASS` 与诊断 UI 预览/保存通过；Python 构建守卫 4/4 通过。
- 加密备份恢复 `BUSINESS_BACKUP_RESTORE_HOST_E2E=PASS`（独立测试 Web 端口 56123、run-id r）：正常 Host 备份、维护恢复审计、合成文件/业务密钥对账、显式活动指针切换、原实例保留、恢复目标正常 Web 启停。成功夹具已由验收器确认停止后精确清理。
- 备份验收修正了旧夹具在首次初始化前注入业务痕迹的顺序，不改变产品初始化保护；一次过长测试路径拒绝同样没有放宽产品限制。两次状态型失败现场留存如下。
- 开发构建同步为无发布编译；不以 BuildOnly 声称另跑了测试。
- 本批 16 个源文件/文档 UTF-8 无 BOM 检查、PowerShell 语法检查及仓库原配置下 `git diff --check` 通过。原有 LF/CRLF 提示和大量未提交/未跟踪改动保留；未提交、推送或修改真实数据。

### 边界与留存

本次没有生成最终发行包，没有原生桌面/DPI/托盘实机或干净 Windows 验收，也没有新增 Docker 验收。未改共享 Python 业务源码、Docker 构建路线、抓取/AI/通知或许可；既有发布矩阵仍须独立验收。历史半初始化实例仍保守拒绝，不承诺自动修复。

负责人本聊天，以下于 **2026-10-04 复核**，不是自动删除：

| 精确仓库相对路径 | 文件数 | 字节数 | 保留理由 |
| --- | ---: | ---: | --- |
| `.tmp/build/launcher-refactor/full/` | 341 | 743,336,942 | 增量编译/复用 |
| `.tmp/build/launcher-refactor/development/` | 341 | 743,339,429 | 隔离开发编译/复用 |
| `.tmp/tests/portable-acceptance/20260927-174608-c98de53522ff/` | 6 | 728,493 | 离屏验证 |
| `.tmp/tests/portable-acceptance/20260927-174922-f0085f7f2930/` | 6 | 728,955 | 离屏验证 |
| `.tmp/tests/portable-acceptance/20260927-175117-4fff22fa9138/` | 6 | 728,223 | 最终完整运行截图 |
| `.tmp/tests/portable-backup-restore-host-e2e/run-runtime-20260927/acceptance-frozen-20260927-p1-r9/` | 12 | 3,376 | 初始化保护拒绝的合成状态证据 |
| `.tmp/tests/portable-backup-restore-host-e2e/run-runtime-20260927-2/acceptance-frozen-20260927-p1-r9/` | 1,385 | 50,612,843 | 过长恢复目标路径拒绝的源实例/备份证据 |

收尾 `.tmp` 可读下限 **35,022,403,889 B / 112,631 files**，**7 处枚举失败**；F 盘可用 **91,175,084,032 B**。`launcher-refactor`/`launcher-development` 测试根无残留文件；成功的真实栈合成夹具已由验收器清理，历史文件和状态型失败夹具未删除。55432/56123 无监听，仅作为端口采样，不替代测试中的逐进程身份核验。


基线：2026-09-27 用户批准的分阶段重构方案；继续遵循 PORTABLE_LAUNCHER_PLAN.md、AGENTS.md 和 REPOSITORY_HYGIENE.md。

## 边界

本次连续收尾预登记（2026-09-27）：统一 Runtime/UI 启动入口与完整源码回归，继续复用已登记的 full/development 缓存、r9 组件及合成测试根，不生成新发行候选。预计同时最多一个 256 MiB 真实栈夹具及 10 MiB 轻量证据；成功且确认停机后精确清理自有夹具，失败保留。负责人本聊天，2026-10-04 复核；开始 F 盘可用 91,230,490,624 B。

追加备份/恢复回归预登记：复用 r9，在 `.tmp/tests/portable-backup-restore-host-e2e/run-runtime-20260927/acceptance-frozen-20260927-p1-r9/` 创建全新合成源实例/备份/恢复目标，预计峰值 1 GiB；不恢复历史夹具、不导入真实数据。现有验收器只在全部通过并确认自有进程停止后精确清理；失败按状态型夹具保留，负责人本聊天，2026-10-04 仅复核。

备份验收第一次在固定测试端口前检拒绝，尚未创建实例；换用探测可用端口后，旧验收器在首次初始化前写入业务文件，被新增初始化保护正确拒绝（PostgresDataUnverified）。保留该失败夹具，不放宽产品保护。验收器改为正常初始化成功后再注入合成业务文件；下一次使用同一父目录下 `run-runtime-20260927-2/acceptance-frozen-20260927-p1-r9/` 全新夹具，同样预计 1 GiB，2026-10-04 复核。

第二个状态型夹具已通过源实例启动和加密备份，但恢复目标因本次过长 run-id 触发现有 PostgreSQL 新实例路径长度限制而拒绝；不放宽产品路径保护。该夹具保留，下一次改用更短且未存在的 `run-r/acceptance-frozen-20260927-p1-r9/`，其余隔离/峰值/清理规则不变。

- 保留 Avalonia / C# / Windows x64 自包含发布及 Docker 路线，不新增第三方依赖。
- 保留实例锁、进程身份、凭据和数据保护；不触碰真实业务数据，不用删除状态文件解决启动问题。
- 现有工作区已有大量未提交/未跟踪实现，本轮仅增量修改，不将旧改动计作本轮成果。
- 不生成新发行候选；源码测试通过不等于成品或公开发行放行。

## 分阶段门槛

| 阶段 | 交付要求 | 状态 |
| --- | --- | --- |
| 0 基线 | 明确回归矩阵、原有缺口和产物归属 | 已记录 |
| 1 诊断 | 入口落盘、阶段与错误分类、无实例也可导出、写失败降级 | 已实现并通过源码/离屏验收；未验收原生崩溃场景 |
| 2 开发验证 | 增量编译、测试不发布、显式隔离开发入口 | 已实现并通过构建、隔离布局及真实栈验收；原生开发窗口待验 |
| 3 编排 | 统一应用服务、分离初始化状态与运行状态 | 已实现：Core Runtime、分配持久化、稳定 Coordinator、端口维护事务、initdb/provision 独立步骤；源码及真实栈回归通过 |
| 4 接线 | UI/集成测试共用产品启动用例 | 已实现：UI 按钮、Host 兼容入口、无界面验收与接管种子共用 Runtime；离屏 UI/真实栈通过 |
| 5 成品 | 最终包、原生桌面、干净 Windows 及适用 Docker 回归 | 待验收；本次源码重构完成不等于发行放行 |

## 必须保留的回归矩阵

- 首启、正常停止、同窗口重启、全停后重开。
- 已分配元数据但未开始初始化时关闭：第二批修复有新分配记录的实例；无可信分配记录的旧半成品仍保守拒绝，不自动迁移。
- 真正 initdb/provision 中断保持拒绝，不自动重建。
- PG 已启动但 Web 失败的安全回收；停止未确认时保护依赖。
- 自动/固定端口冲突及切换失败；原进程接管、部分存活、未知身份拒绝。
- 缺组件、端口拒绝、子进程早退、诊断路径不可写、UI 初始化失败。
- 日志与导出不得包含异常原文、路径、参数、环境、凭据或业务请求；必须保留可定位的受控阶段、系统错误码及退出码。

## 本轮临时产物登记

生产者：本聊天。用途：增量编译与合成诊断/开发入口验收。共享 SDK、NuGet、PG/Python 缓存只复用，不归本轮清理。

- `.tmp/build/launcher-refactor/`：可重建增量编译缓存，预计峰值 1.5 GiB；保留供后续重构，2026-10-04 复核。
- `.tmp/tests/launcher-refactor/`：合成测试结果与必要诊断，预计 100 MiB；轻量证据 2026-10-04 复核；状态型失败夹具不得自动删除。
- `.tmp/tests/launcher-development/`：仅显式开发操作创建的隔离实例；含数据库时按状态型夹具保护，不作为自动清理对象。
- 复用验收器会在 `.tmp/tests/automatic-port-ui/中文 空格-<本轮GUID>/` 创建合成实例（预计 256 MiB），成功且确认正常停止后由验收器精确清理；失败状态保留，2026-10-04 只复核。
- 默认测试驱动另在 `.tmp/tests/portable-acceptance/<本轮时间戳>/` 留下离屏截图（预计 5 MiB），以及 `.tmp/tests/portable-dotnet-runner/<本轮时间戳>/` 有界捕获；后者成功时由原有运行器清理，失败保留。

开始时 `.tmp` 可读下限 33,475,635,182 B / 110,474 files，7 处枚举失败；F 盘可用 92,746,084,352 B。未清理历史目录。

## 第一批交付验收（2026-09-27）

本批仅交付阶段 0–2 的上述范围，不代表整个架构重构完成，不替代最终包验收。

第二批临时空间预登记：复用同一增量缓存和已登记的合成验收根，修复分配后未初始化即关闭的重开缺口，收拢 Host 启动策略。不新增依赖或候选包；预计新增合成夹具峰值 256 MiB，成功停止后由原验收器精确清理，失败状态仅保留至 2026-10-04 复核，不自动删除。

- 开发与普通编译分别运行 `-NoPublish -Incremental` 工作流（开发另加 `-DevelopmentBuild`）：构建 0 警告、0 错误；Core、HTTP 契约、Python 生命周期、Windows 进程、离屏 UI 和启动诊断验收通过。
- 启动诊断测试覆盖不可写目录降级、受控错误码/退出码、无敏感原文、有界存储与内存、不覆盖文件、并发事件、观察者失败隔离，以及无 Host 仍可导出。
- 开发/发行两种构建的 `--build-flavor-acceptance` 均通过；直接调用 `RejectDevelopmentPublish` 目标按预期拒绝开发发布，未实际 publish。
- `--development-layout-acceptance` 复用既有 r9 组件通过：隔离路径、会话复用、越界拒绝；不创建数据库。此入口使用新 C#、冻结后端，不是 Python 源码热加载。
- 新编译程序集复用 `launcher/dist/portable-acceptance-frozen-20260927-p1-r9`，运行 `--automatic-port-real-ui` 获得 `AUTO_PORT_REAL_UI=PASS`：真实 PG/Web 自动避让、同窗口重启、固定端口冲突、既有自动端口冲突、全停后重开。成功合成数据库由原验收器清理。
- 最终普通编译的诊断 UI 预览/保存与诊断导出器验收通过，新增构建脚本守卫测试 3/3 通过。一次人工验证误指定不存在的诊断 UI 成功标记，修正为该测试实际的退出码契约后通过；不是产品失败。
- 按 review-swarm 聚焦只读复核发现并修复 UI 按高水位丢弃乱序诊断的问题；新增倒序/重复事件回归通过，复核关闭，无新增发现。
- 原生开发窗口、干净 Windows、新发行包及 Docker 本轮未验收；未新增依赖、未生成新候选、未改动真实业务数据。既有“分配后尚未初始化即关闭”缺陷仍保留在阶段 3。

### 收尾与留存

负责人：本聊天；下列缓存用于下一阶段增量开发，截图用于本次离屏验收证据，均于 **2026-10-04 复核**，不是届时自动删除。

| 精确仓库相对路径 | 文件数 | 字节数 | 用途 |
| --- | ---: | ---: | --- |
| `.tmp/build/launcher-refactor/development/` | 341 | 742,752,673 | 开发增量缓存 |
| `.tmp/build/launcher-refactor/full/` | 341 | 742,749,162 | 普通编译增量缓存 |
| `.tmp/tests/portable-acceptance/20260927-160021-5a393a744554/` | 6 | 729,154 | 开发构建离屏截图 |
| `.tmp/tests/portable-acceptance/20260927-160821-02b9c6bef868/` | 6 | 729,960 | 普通构建离屏截图 |

收尾盘点 `.tmp` 可读下限 **34,962,596,131 B / 111,168 files**，仍有 **7 处枚举失败**，不将未读项视为空；F 盘可用 **91,253,141,504 B**。本批 `.tmp/tests/launcher-refactor/` 与 `.tmp/tests/launcher-development/` 无残留文件，成功合成诊断及运行器捕获已清理；未删除历史候选、缓存或状态型失败夹具。55432/58000 无监听，但不以这两个端口检查代替全部进程归属证明。

`git diff --check` 通过（存在既有 LF/CRLF 提示）；原工作区大量改动及未跟踪目录保持原状，未提交或推送。此检查不覆盖未跟踪文件内容，另对本批文件执行 UTF-8 无 BOM 和脚本语法检查。

## 第二批交付验收（2026-09-27，阶段 3 的首个切片）

第三批临时空间预登记：继续复用 `.tmp/build/launcher-refactor/{full,development}` 与已有测试根；本聊天负责，2026-10-04 复核。新增轻量测试/截图预计 10 MiB，真实栈合成夹具峰值约 256 MiB，成功且安全停机后由原验收器清理，失败状态保留，不新增运行时副本或候选包。

- 新增 `PortableInitializationState`，在持锁完成基础分配后持久化 `Allocated`，在数据库初始化前以原子文件提交转换为 `InitializationStarted`。它不是数据库完成或 Ready 标记；已有 PG/provision/进程身份校验继续保留。
- 只有身份匹配、路径安全且无 PGDATA/初始化/进程/业务痕迹的分配实例可以等待手动首启。中断、未知状态、空 PGDATA、损坏记录及旧无记录半成品不会获得初始化许可；不自动补凭据、补标记或重建。
- UI 普通启动/重启统一调用 Host `StartAsync`，不再依据 UI 快照选择首启/重启策略；实例存在检查移至 Host。数据库启动边界的同一检查同时覆盖端口切换、回滚与直接 Coordinator 启动。
- 重开未初始化实例不会触发已初始化实例的自动启动偏好。完整旧实例无新 journal 时仍兼容原恢复链路；分配记录提交前的崩溃仍保守拒绝，本批不承诺任意断电点可自动继续。
- 最终 `-NoPublish -Incremental` 构建 0 警告/0 错误，Core、HTTP、Python 生命周期、Windows 进程、UI smoke、启动诊断全部通过；Windows 套件已包含新状态守卫测试。Python 构建守卫 3/3 通过。开发构建 `-BuildOnly -Incremental -DevelopmentBuild` 同样通过，未宣称此命令运行测试。
- 最终程序集复用 r9 的 `--automatic-port-real-ui` 返回 `AUTO_PORT_REAL_UI=PASS`。新增覆盖：分配后关闭、两次重开、凭据与实例 ID 不变、未初始化不自动启动、首启消耗许可、完整实例缺少集群标记时拒绝启动，以及无 journal 的完整旧实例正常重开。原自动避让/固定冲突/同窗口重启矩阵继续通过。
- 同一真实 Host 验收新增 6 类只含合成元数据的拒绝夹具：init 进度、provision 进度、运行记录、空 PGDATA、已开始阶段但无数据库、旧无 journal 半成品。验证拒绝后所有文件字节不变、分类 lease 释放。真实成功夹具及合成拒绝夹具已由验收器清理；未动历史失败夹具。
- 尚未完成：完整 `LauncherRuntime`、把 provision 从长期运行组件拆出、稳定 Coordinator 身份及去除 UI 摘挂门控、原生窗口/干净 Windows/新成品/Docker 验收。本批不是全量架构重构完成。

### 第二批留存

负责人仍为本聊天，2026-10-04 复核；缓存供下一步复用，截图为本轮源码离屏验收证据，未设置自动删除。

| 精确仓库相对路径 | 文件数 | 字节数 |
| --- | ---: | ---: |
| `.tmp/build/launcher-refactor/development/` | 341 | 742,820,817 |
| `.tmp/build/launcher-refactor/full/` | 341 | 742,815,778 |
| `.tmp/tests/portable-acceptance/20260927-162409-90b9891f4e10/` | 6 | 728,939 |
| `.tmp/tests/portable-acceptance/20260927-162902-0da617b6bb49/` | 6 | 729,108 |
| `.tmp/tests/portable-acceptance/20260927-163203-58349055ef4c/` | 6 | 729,142 |

第二批收尾 `.tmp` 可读下限 **34,964,918,080 B / 111,186 files**，**7 处枚举失败**；F 盘可用 **91,239,387,136 B**。`launcher-refactor`/`launcher-development` 两个测试根无残留文件，55432/58000 无监听。Git 差异检查和本批 8 个源文件/文档的 UTF-8 无 BOM 检查通过；原有改动保持不动，无提交或推送。

## 第三批交付验收（2026-09-27，稳定 Coordinator）

第四批临时空间预登记：本聊天复用现有 full/development 增量缓存及 r9，拆分 provision 启动步骤；轻量截图预计 10 MiB，真实合成夹具峰值 256 MiB，成功安全停止后精确清理、失败保留。负责人本聊天，2026-10-04 复核，不自动删除。开始前沿用第三批收尾盘点，下次重型测试前再次检查磁盘。

第四批追加恢复验收登记：复用现有 `--host-recovery-acceptance`，在 `.tmp/tests/host-recovery/<本轮GUID>/` 建立独立合成实例，验证旧 Launcher 退出后的原进程接管；峰值约 256 MiB，不与其他真实 PG 测试并行。成功由验收器在停机后精确清理，失败保留，2026-10-04 复核；不操作历史夹具。

- Host 生命周期内持有同一个 Coordinator。新增 `RunMaintenanceAsync` 作用域，在同一操作锁内执行组件替换、启动与停止；仅允许全部组件确认停止后的同 ID 替换，保留注册策略、原日志和订阅。过期或并发使用维护会话被拒绝；关闭等待事务收尾。
- 端口切换、自动避让及回滚改用该维护会话，删除 `IPortableHostCoordinatorReplacementGate` 和 UI 摘挂实现。平台层不再调用 UI 门控，也不重建 Coordinator；既有 intent、revision、身份、Ready 和无法确认停机时的保护继续保留。
- UI 只负责用户操作互斥、旧业务会话失效及结果刷新；事务开始提升预览代次但不更换 Coordinator，拒绝旧诊断预览。刷新失败保持 fail-closed，正常及异常路径均释放 UI 操作门控。
- 最终 `-NoPublish -Incremental` 通过：构建 0 警告/0 错误，Core、HTTP、Python 生命周期、Windows 进程、UI smoke、启动诊断全部通过。独立 Web 端口 UI 和诊断预览测试通过，Python 构建守卫 3/3 通过。开发缓存通过 `-BuildOnly -Incremental -DevelopmentBuild` 同步，后者只编译、不运行测试。
- 新维护会话测试覆盖日志/订阅延续、并发启动/停止拒绝、错误 ID/过期引用/未知或运行中组件拒绝、会话失效、异常/取消释放门控，以及关闭期间等待与安全停止。
- 最终真实 PG/Web 验收复用 r9 返回 `AUTO_PORT_REAL_UI=PASS`，除前两批矩阵外增加固定占用端口回滚及新端口成功应用；断言同一 Coordinator、原日志保留、原订阅持续接收 Running 事件及旧预览代次失效。成功合成夹具在安全停机后由原验收器精确清理，不触碰历史失败状态。
- 真实栈首轮在创建夹具之前的预检出现一次 `IOException`，原因未确定；已增加路径、磁盘、组件、PG/Web 端口和夹具的细分预检标签，后续重跑通过。不把首次失败解释为已证实的环境问题。
- 未完成：完整 `LauncherRuntime` 统一用例、provision 从长期组件拆出、原生开发窗口/干净 Windows/新包/Docker 验收。本批不是全量重构完成；无新依赖、新发行候选、提交或推送，未改业务行为或真实数据。

### 第三批留存

负责人：本聊天；缓存用于后续增量迭代，截图用于离屏验收，**2026-10-04 复核**，不自动删除。复用共享依赖与既有 r9，未产生新运行时副本。

| 精确仓库相对路径 | 文件数 | 字节数 |
| --- | ---: | ---: |
| `.tmp/build/launcher-refactor/development/` | 341 | 742,887,593 |
| `.tmp/build/launcher-refactor/full/` | 341 | 742,885,122 |
| `.tmp/tests/portable-acceptance/20260927-164731-e271d2ba8d83/` | 6 | 729,153 |
| `.tmp/tests/portable-acceptance/20260927-165638-ec94c6ad36c0/` | 6 | 728,970 |

第三批收尾 `.tmp` 可读下限 **34,966,512,323 B / 111,198 files**，**7 处枚举失败**，未读项不视为空；F 盘可用 **91,232,296,960 B**。`launcher-refactor`/`launcher-development` 测试根无残留文件；本批成功夹具和有界运行捕获已清理，历史目录未删除。Git 差异检查通过（既有 LF/CRLF 提示保留）；未跟踪文件另作 UTF-8 无 BOM 检查。

## 第四批交付验收（2026-09-27，provision 独立启动步骤）

第五批临时空间预登记：本聊天继续复用 full/development 缓存、r9 与既有合成测试根，拆出 initdb 步骤及辅助进程安全观察。预计轻量证据 10 MiB、每个串行真实夹具峰值 256 MiB；新增目录限于 `.tmp/tests/launcher-refactor/`、现有自动端口/恢复验收 GUID 根和离屏截图时间戳根。成功安全停止后精确清理，失败保留；2026-10-04 复核，不自动删除。基线沿用第四批收尾盘点，不新增候选和依赖。

第五批补充：启用现有 PostgreSQL 集成套件，夹具位于 `.tmp/tests/portable-acceptance/<本批时间戳>/postgres/<GUID>/`，多案例累计峰值预计 768 MiB；复用锁定 PG 缓存。成功安全停止后按原精确边界清理，失败状态保留，2026-10-04 仅复核。运行前 F 盘可用 91,226,468,352 B。

- Core 新增 `ILauncherStartupStep` 与独立步骤快照：NotRun / Executing / Completed / Failed，另记录辅助进程是否确认退出。步骤按指定后续服务插入同一受锁启动序列，不注册为长期组件，不参与伪造的 Running/Stopped 循环。
- `WindowsPortableProvisionStep` 从 Host 大文件提取。真实组件只保留 PostgreSQL 与 Web；PG 后、Web 前执行 provision 或核验原完成记录。UI 普通启动、端口候选及回滚共用该序列，不修改后端初始化命令、凭据或持久记录格式。
- 步骤成功后正常停机不清除 Completed。辅助进程存活、归属不明或观察出错时，停止/回滚保留它依赖的 PG，关闭不可报告安全，Host 释放前重新观察服务和辅助进程。备份在停写后再次验证辅助进程已退出。接管只核验完成记录，不执行准备命令。
- UI 用两个预期服务 ID 检查运行组件，准备步骤独立显示“待核验／准备中／已完成／准备失败”；尚未确认辅助进程退出时禁用端口和启动前检，保留关闭保护。诊断导出另列白名单步骤状态，不导出名称、异常原文或业务数据。
- 最终 `-NoPublish -Incremental`：0 警告/0 错误，Core、HTTP、Python 生命周期、Windows 进程、UI smoke、启动诊断通过。新增 Core 用例覆盖步骤执行顺序、停止不撤销完成、备份顺序、准备失败不启动 Web、未知辅助进程保护 PG、不可中断步骤取消等待、被动接管与观察失败；独立端口 UI 测试包含未确认辅助进程场景，返回 `WEB_PORT_UI_ACCEPTANCE=PASS`。
- 脱敏诊断导出和诊断 UI 测试通过；新增步骤白名单及敏感名称排除断言。Python 构建守卫 3/3 通过。`-BuildOnly -Incremental -DevelopmentBuild` 同步缓存成功，仅编译、未运行开发构建测试。
- 复用 r9 的 `--automatic-port-real-ui` 返回 `AUTO_PORT_REAL_UI=PASS`：保留前三批矩阵，新增真实 Host 仅有两个长期服务、UI provision 显示已完成、停止不撤销完成、重启不改写 provision 完成记录。最后的备份辅助进程复检改动另由最终 Core 回归覆盖，本矩阵未执行真实加密备份。
- 复用 r9 的 `--host-recovery-acceptance` 返回 `HOST_UI_RECOVERY=PASS`：旧 Launcher 进程退出后的原 PG/Web 身份与 Ready 接管、恢复后停止/重启、全停重开，以及损坏 provision、PG 单独存活和未知运行记录拒绝。两个真实栈验收不并行，成功合成夹具由原验收器安全停机后精确清理。
- 未完成：完整 `LauncherRuntime` 应用服务与 UI 解耦，initdb 从 PG 启动适配器进一步拆分；真实备份/恢复的冻结包复跑、原生窗口、干净 Windows、新包及 Docker 放行仍待验收。本批不宣称整个重构完成，不新增依赖或发行候选，不改动真实业务数据、抓取、AI 和通知行为。

### 第四批留存

负责人本聊天；复用缓存与离屏证据于 **2026-10-04 复核**，不自动删除。未删除历史目录或历史失败夹具。

| 精确仓库相对路径 | 文件数 | 字节数 |
| --- | ---: | ---: |
| `.tmp/build/launcher-refactor/development/` | 341 | 743,154,429 |
| `.tmp/build/launcher-refactor/full/` | 341 | 743,150,414 |
| `.tmp/tests/portable-acceptance/20260927-170821-4ca970038b49/` | 6 | 729,080 |
| `.tmp/tests/portable-acceptance/20260927-171308-8c9f34ae8132/` | 6 | 728,372 |

收尾 `.tmp` 可读下限 **34,968,501,903 B / 111,210 files**，**7 处枚举失败**；F 盘可用 **91,227,033,600 B**。`launcher-refactor`/`launcher-development` 测试根均无残留文件，55432/58000 无监听，不将两个端口检查当作全机无进程证明。`git diff --check` 通过（保留既有 LF/CRLF 提示），本批 15 个源文件/文档的 UTF-8 无 BOM 检查通过；未提交或推送。

## 第五批交付验收（2026-09-27，initdb 独立编排步骤）

- 新增 `WindowsPostgresInitializationStep`，注册在 PG 服务之前，删除 `InitializedPostgresComponent` 组合包装类。编排为 initdb 准备 → PG 主进程 → provision 准备 → Web 主进程；Windows PG 适配器继续实现底层命令、集群校验与原有磁盘格式。
- 初始化步骤有独立快照、日志与白名单诊断，映射到 PostgreSQL 安全错误类别。接管只调用只读完成核验，不能执行初始化或分配。
- 对 initdb 完成至主进程启动之间的新增安全点补充本控制器内存证明：仅亲自完成初始化、尚未尝试启动 postmaster、标记一致且无 PID 文件时确认 Stopped。重开不能继承该证明；有标记而无持久运行记录的旧实例仍 Unknown，不自动推断可启动。
- 跟踪 initdb 辅助进程身份；执行中、辅助进程尚未确认退出、旧中断记录存在或观察失败时，不能宣称准备已静止。主进程启动、Host 释放与备份核验增加该保护，不强杀辅助进程、不自动重跑 initdb。
- `-NoPublish -Incremental -PostgresIntegration` 通过，0 警告/0 错误。Core、HTTP、Python 生命周期、Windows 进程、UI smoke、启动诊断通过；真实 PG 集成套件 6 项通过。新增用例主动在 initdb 完成后取消，断言没有主进程/运行记录、安全关机、被动核验不改写标记、重开不继承内存证明、未知初始化记录拒绝核验和启动且原文件不变。
- PostgreSQL 测试驱动改为失败时保留夹具；成功正常停机后的精确清理维持原有边界。本批无失败夹具产生。
- `--automatic-port-real-ui` 复用 r9 返回 `AUTO_PORT_REAL_UI=PASS`：前四批矩阵继续通过，真实 Host 的两个准备步骤均 Completed，UI 分别显示完成，停止/重启保留完成状态。`--host-recovery-acceptance` 返回 `HOST_UI_RECOVERY=PASS`，覆盖原进程接管、正常停止/重启及损坏记录/部分运行/未知身份拒绝。真实 PG、自动端口、恢复验收串行运行，成功合成状态已安全清理。
- 独立端口 UI 返回 `WEB_PORT_UI_ACCEPTANCE=PASS`；诊断导出新增初始化步骤白名单断言并通过，Python 构建守卫 3/3 通过。开发缓存通过 `-BuildOnly -Incremental -DevelopmentBuild` 同步，后者只编译。
- 尚未完成：完整 `LauncherRuntime`。本轮核对确认自动端口避让、固定端口前检和 Ready 后处理仍由 UI 编排，下一批须迁入统一用例并保留 Host 事务锁与 revision 检查，不能仅加转发层。原生窗口、干净 Windows、最终新包、冻结包备份/恢复及适用 Docker 回归仍是放行门槛。
- 未新增依赖、发行候选或业务行为改动；未触碰真实数据、历史候选和失败状态，未提交或推送。

### 第五批留存

负责人本聊天；下列增量缓存与离屏证据于 **2026-10-04 复核**，不自动删除。

| 精确仓库相对路径 | 文件数 | 字节数 |
| --- | ---: | ---: |
| `.tmp/build/launcher-refactor/development/` | 341 | 743,204,301 |
| `.tmp/build/launcher-refactor/full/` | 341 | 743,201,858 |
| `.tmp/tests/portable-acceptance/20260927-172500-926453609f31/` | 6 | 728,568 |

收尾 `.tmp` 可读下限 **34,969,331,787 B / 111,216 files**，**7 处枚举失败**；F 盘可用 **91,230,494,720 B**。`launcher-refactor`/`launcher-development` 测试根无残留文件，55432/58000 无监听；不以端口采样替代全机进程证明。未删除历史目录。
