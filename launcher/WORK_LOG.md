# 便携 Launcher 自动落地记录

## 2026-09-27：1.1.0.0-beta 源码提交

- 根据项目所有者决定，以 r10 本机启动验收作为 beta 基线；版本、当前范围及已知限制见 [版本说明](../docs/RELEASE_1.1.0.0-beta.md)。以下旧批次“不提交”仅记录当时决定。
- 后端与 Launcher 统一版本；Windows 数字文件版本为 `1.1.0.0`，诊断保留 `1.1.0.0-beta`。旧 r10 ZIP 未改名、未覆盖，本次没有生成新 ZIP 或上传发布。
- 本次增量非发布构建零警告、零错误；Core、HTTP、Python 生命周期、Windows 进程、UI、启动诊断、端口 UI 回归通过，诊断导出完整信息版本检查通过。
- 后端便携单元测试 237 项、版本一致性 2 项、前端定向 2 项通过；构建/扫描工具测试 36 项中 35 项通过、1 项跳过。维护接口测试改用当前版本常量，避免硬编码旧版本。
- 补充日志保留、路径隔离、存储导入及 PostgreSQL 配置归属测试：24 通过、2 跳过、5 子用例通过。沙箱内 Restart Manager 会话被拒导致的 3 项失败，在获准沙箱外重跑后全部通过，未修改文件占用保护。
- 固定 SHA-256 的第三方许可原文禁止 Git 换行转换；索引与原始字节及登记 SHA-256 一致。Chromium 上游许可原文的空白保留，不为通过空白检查改动证据。

## 2026-09-27 生命周期、自动端口修复与 r7

- 修复 provision 已退出辅助进程身份残留，以及停止后验证提前标 Running 的两个生命周期问题；保留未知进程、全停、lease、CAS/intent 和 UI 门控保护。
- Web 新实例默认自动、旧配置保持固定；空闲复用原端口，冲突后只启动一个候选，Ready/身份通过才更新实际地址。运行中保存模式不重启；PG 不自动改端口。
- 最终官方构建 `20260927-000727-6c4c68884fd5`，0 warnings/errors；r7 scanner 无 findings，ZIP SHA-256 `efa7dba165a4a8fd9be9e5ff36111781f4caf3eb32ff05d851e198e7be0d997d`。包内实际程序集 `BUNDLE_ACCEPTANCE`、`WEB_PORT_HOST_ACCEPTANCE`、`AUTO_PORT_REAL_UI`、`CONFIG_PG_CAS_ACCEPTANCE` 均 PASS。
- 备份恢复首次末端进程审计与并行扫描冲突；其后两次独立串行分别在恢复初始化/备份停机阶段失败，最终独立运行 PASS。未证实两个串行失败根因，仍视稳定性阻断，保留失败夹具。新增严格 run-id 隔离及脱敏诊断，仅修改测试代码。
- Docker 历史 9 月 24 日 PASS 保留，本轮不重验。原生 Windows/Explorer/DPI、干净 Windows、跨 SID 未验。r7 仍为 preview-integration，不提交、不发布；详细证据和收尾见 `docs/PORTABLE_PORT_FIXES_20260927.md`。

## 2026-09-26 发布前补验：r6 暂不放行

- 官方构建 `20260926-155625-5911cc87caf4` 的集成、两周期重建 Host、Headless UI、自包含入口通过；r6 scanner/ZIP 全校验通过。追加端口 UI、托盘 Headless、恢复 UI、业务连接、偏好、诊断 UI 专项通过。
- 真实端口应用发现新端口启动及旧端口恢复均失败；最终使用 r6 包内 Core/Windows DLL 在隔离 harness 复现 `PortableWebPortApplyException` 与 `DatabaseProvisionInterrupted`。初次 provision 的 `_helperIdentity` 在停止后仍保留，再启动的完成状态校验直接拒绝非空身份。未修改产品逻辑。
- 仅修正验收入口：明确允许 r6 指纹、纠正 setup/control token 断言、增加脱敏诊断。失败夹具保留，编译中间件精确清理 822,821,890 B。详细证据、范围和留存见 `docs/PORTABLE_RELEASE_ACCEPTANCE_20260926.md`。
- Docker 仅 Compose config 通过，引擎未运行；原生桌面、干净 Windows、跨 SID 未验。未提交、未发布，不以较早 PASS 覆盖新阻断项。

## 2026-09-26 审查问题修复

- 修复进程遗留 PID、恢复 lease/文件计数/候选目录、Web 配置并发及偏好保存、local AI 缓存、离线下载与导出隐式删除问题，并提前构建磁盘检查。详细范围和验证见 `docs/PORTABLE_REVIEW_FIXES_20260926.md`。
- 官方 `-PythonIntegration` 构建 `20260926-150233-6fde03f25d19` 通过，0 warnings/errors，包含 Windows 恢复协议 0/1/2/3 文件和失败后取消回归、Headless UI、真实嵌入 Python 栈。Python 定向 130 passed / 2 skipped / 30 subtests，Node 2 passed，PowerShell guards 2 passed。
- r6 完整包/ZIP 已构建、扫描，实际包真实 Host→PG CAS 和业务备份恢复分别输出精确 PASS，ZIP SHA-256 `b292e8976f01ecd44078fbbee01f6c97071a5540b19e16c3dc8783e9b5433843`。详细字节数、留存和清理见修复记录。
- Docker 引擎未运行，本轮未复跑容器；原生/干净 Windows/跨 SID 仍未验，不据此公开发布。自有可重建编译中间件已清理 822,814,778 B；保留候选和轻量证据至 2026-10-03 复核。

## 2026-09-25 Launcher 视觉 v3 与功能核对

- 用户要求继续优化 Aki 风格并核对原约定。首页改成短横幅、合并状态/组件区、快捷入口与当前账号摘要并排；侧栏统一居中和选中态，控制台/设置锚定底部。AI 配置独立页面，未连接时显示三步说明；启动偏好使用 ToggleSwitch。保留原有业务与安全 handler；无新依赖、无外部 AI 请求。
- 功能清单见 `docs/LAUNCHER_UI_FEATURE_AUDIT.md`。版本管理、日志筛选/暂停滚动、任务/通知摘要与后续用量增强仍存在缺口，本轮未假称补齐。
- 最终源码构建 ID `20260925-030040-50ff2f50da6f`：官方构建脚本 `-PythonIntegration`，Release 0 warnings/errors；Core、HTTP、Python 生命周期、Windows 进程、真实 Python 栈、UI Smoke 与自包含入口全部通过。UI Smoke 覆盖新增 AI 页面与配置错误焦点，已查看首页 1120×760/920×640、设置及 AI 页面离屏图。最小窗口正常纵向滚动，主操作固定可见。
- 完整包：`launcher/dist/portable-visual-v3-20260925/`，8,079 文件 / 1,091,422,293 B。ZIP 415,943,425 B，SHA-256 `8445e591c91f895bed79431fbc9f41b3bafb54f5b6e6b9d1d775cc7d856a9009`。builder 完成 member/CRC/hash 校验，独立 scanner PASS；默认 EXE 为真实模式，包内无 data。未在这个包启动真实 PG/Web，未将 r5 E2E 结果冒充本包结果；原生窗口、多 DPI 与干净 Windows 仍待验收。
- 首轮构建 `20260925-025443-4ed28a720c56` 与最终构建/测试留作视觉验收对比，共约 1.534 GiB；精确路径、用途、字节数和 2026-10-02 复核日期见 `.tmp/build/launcher-layout-v3/RETENTION.md`。两份 Launcher 输入和完整包/ZIP 同日复核，保留作为验收候选；未删除历史文件或业务状态。收尾 `.tmp` 可读部分 27,856,878,131 B，7 个既有目录拒绝访问；F 盘空闲 52,973,330,432 B。

## 2026-09-24 最新验收 checkpoint：r5 preview-integration

- r5 实际包 `launcher/dist/portable-acceptance-frozen-20260924-p1-r5/` 共 8,079 个文件 / 1,089,294,204 B；同名 ZIP 为 413,849,907 B，SHA-256 `9f01503a2570f4b121143646979461ec83fcd5a6c1e7871567434e6ee605b91d`。Launcher Core/HTTP/Python/Windows/真实栈/UI Smoke 构建与回归均 PASS，0 warnings/errors。发行扫描覆盖 8,077 payload entries、findings=[]；ZIP member/CRC/size/hash 校验 PASS；8,077 包输入与 111 控制输入/HEAD 前后指纹一致。
- r5 实际 Host→PG 配置 CAS E2E 一次 PASS：精确输出 `CONFIG_PG_CAS_ACCEPTANCE=PASS`，Web/PG 端口 46123/55432。
- r5 实际 Host→PG 备份恢复 E2E 一次 PASS：精确输出 `BUSINESS_BACKUP_RESTORE_HOST_E2E=PASS`，端口 58500/55432。旧 retained fixture 为 2,756 files / 100,725,670 B，本轮相同清单算法前后 SHA-256 `cf2a7fba072af94ee24bc132b4fda2233878a9c22d51c82857eb1e20c30571c8` 未变。两轮 ZIP hash 未变；成功 fixture 与 capture 已安全自清，无 PostgreSQL/Python/Launcher 残留，相关端口已释放。
- 初始化中断只读分类/恢复指引实现已通过独立审查及 Core/Windows fake/Headless tests；尚无真实断电演练，不计完整故障注入 PASS。
- Docker 冻结源码回归 PASS（Engine 29.7.2）：Docker 有效 build context 前后均为 315 files / 74,195,395 B，SHA-256 `8436ce52d43b6ebfb5e0f95172e0ea31db755ad6f7733ec4521163ede0300bde`。独立合成 app+PostgreSQL normal lifespan、PG healthy、schema ensure、health 200、login 200、bad login 401 均通过；原 keygate app/PG 容器 ID 健康状态及四卷未变。本轮唯一容器、空卷、网络、镜像标签和 3 文件 / 75,594 B 合成 fixture 已精确清理，BuildKit cache 未 prune。该 Docker 证据不构成 Windows r5 公开放行。
- r5 仍为 `preview-integration`，不公开放行；原生 Avalonia 窗口、Explorer tray、多档 DPI、干净 Windows x64 与跨 SID 恢复仍缺证据。r3/r4 内容仅作历史，不覆盖 r5 权威结果。

## 2026-09-24 最新验收 checkpoint：r4 备份恢复与预览候选

- r4 实际包真实 PG 配置 CAS E2E 已 PASS：在 no-popup 有界 runner、隔离合成 fixture、备用 Web 端口 46123 / PostgreSQL 端口 55432 下，探针精确输出 `CONFIG_PG_CAS_ACCEPTANCE=PASS`。运行前后 ZIP SHA-256 均为 `fbc17158ba3c1235a76f3c10847c1f39b516bc835951995e6dba94d44d381ddd`；测试后端口已释放，无 PostgreSQL/Python/Launcher 残留。成功 fixture/capture 已由验收 harness 清理；此前失败 fixtures 仍保留，不能记为已清理。探针排除了两个假失败：private probe 环境遗漏 `PATH`；PostgreSQL `inet_server_addr()::text` 返回地址可能带 `/32`，改用 `host(inet_server_addr())` 后仍严格核对 `127.0.0.1`/`::1` 与 DSN port；PostgreSQL 17 官方文档支持该 `host()` 用法。
- r4 实际包上的 Host→PG 业务备份/恢复 E2E 已 PASS：运行器精确输出 `BUSINESS_BACKUP_RESTORE_HOST_E2E=PASS` 且 exit 0，备份后恢复到隔离目标并完成数据库逐表、业务文件、恢复密钥和目标实例登录核对。运行前后 ZIP SHA-256 均为 `fbc17158ba3c1235a76f3c10847c1f39b516bc835951995e6dba94d44d381ddd`；旧 retained fixture inventory（2,756 files / 100,725,670 B）SHA-256 `38554128...53aab6` 未变。PG/Python/dotnet/Launcher 相关进程与 55432/58500 listener 数均为 0；本次成功 r4 fixture 与 runner 临时捕获已清理，旧 r1 包与其数据未触碰。r3 的备份恢复通过仍是独立历史证据。
- 备份恢复密钥路径已实现：口令保护的 `recovery-keys.json` 携带两个业务密钥；恢复时创建新的 PostgreSQL 角色口令，并由当前 Windows 用户 DPAPI 重新保护密钥。r3 E2E 证明同一 Windows 用户/SID 下恢复成功；另一 Windows 用户或另一台机器的实际演练仍缺，因此不标跨用户/跨机器验收完成。
- 诊断预览与保存已接入生产 MainWindow/ViewModel：`WebPortUiAcceptance.RunDiagnosticPreviewAcceptanceAsync` 通过真实按钮/handler 的 Headless 流程，验证预览后取消不写盘、选择目录后保存字节与预览 ticket 内容逐字节一致、失败脱敏及 ticket 清理。该证据不覆盖原生窗口和 Explorer 操作，二者仍待验。
- r4 预览候选目录 `launcher/dist/portable-acceptance-frozen-20260924-p1-r4/` 含 8,079 个文件、1,089,287,356 B；同名 ZIP 为 413,846,739 B，SHA-256 `fbc17158ba3c1235a76f3c10847c1f39b516bc835951995e6dba94d44d381ddd`。扫描确认 8,077 payload entries；ZIP 校验通过；包指纹和控制指纹均与预期一致、未发生漂移。它仍是预览候选，不表示公开发布放行。
- 真实 PG CAS E2E 后续复测已 PASS，详见本 checkpoint 首条；以下旧失败结果保留为历史诊断：`import-settings-manager/Other`。相关失败 fixtures 仍保留供诊断。
- Docker 隔离 Compose 的 normal-lifespan health/login smoke 曾 PASS，但全量上下文指纹有漂移；该回归不能证明对应最终冻结源码，需在指纹一致的冻结源码上复跑。
- 干净 Windows x64 无开发工具环境、原生 Avalonia 窗口、Explorer 托盘与多档 DPI 仍未验收。r4 包扫描/ZIP 校验和 r3 备份恢复通过均不替代这些 P1 门槛；当前不标公开 P1 PASS。

## 2026-09-24 前一验收 checkpoint：r3 候选与后续修复

> 历史快照：以下内容记录 r3 备份恢复和 r4 预览包验证前的状态；与上方最新 checkpoint 不一致时以上方为准。

- 当前仍未放行公开 P1。候选目录 `launcher/dist/portable-acceptance-frozen-20260924-p1-r3/` 共 8,079 文件 / 1,089,280,484 B；ZIP `launcher/dist/portable-acceptance-frozen-20260924-p1-r3.zip` 为 413,844,346 B，SHA-256 `1d00d1524f5cc0a1c5c9f867f6c1d77f03b20673f226edff0c1c328a172d5d48`。独立包扫描 8,077 payload entries PASS；实际包 Host→PG 两个隔离启停周期 PASS。Launcher 与构建器后续修复已落入当前源码，须重建 r4 并重验，不能把 r3 当最终发行包。
- Docker 隔离 Compose 使用合成配置、独立 project/PG 卷，正常 lifespan 下 app+PostgreSQL health/login smoke PASS；期间没有改动原有容器或卷。全量上下文指纹发生漂移，因此此证据不能证明最终冻结源码一致；需要冻结后复跑并核对指纹。
- UI Smoke 反复弹出 `dotnet.exe` Windows 错误框的根因已查明：测试断言异常未被 UI Smoke 入口捕获，异常逃逸到原生 WER。Smoke 与 Platform.Windows.Tests 入口已加入托管异常捕获；构建脚本在运行 .NET 前检查 no-WER 模式；后续 .NET 测试只能通过 `scripts/portable/windows_dotnet_runner.py` 安全运行器执行。运行器限定 Job、超时和输出上限并清理原始输出、要求精确成功标记；SDK API 烟测和首次 AI 配置 fake 测试通过，未再弹窗。不要直接启动测试 DLL。
- Launcher 最终审查修复：安全退出等待期间将“取消退出”门控到确实可恢复的阶段，避免停止操作已不可撤销时 UI 仍允许取消；发行清单现强制要求 Launcher inventory 项，避免漏列 Launcher 自身。对应静态检查与定向测试通过；合并入下一轮组合包后仍须跑完整扫描和 E2E。
- 真实 PG 配置 CAS E2E 仍阻断：此前探针结果 `stage=probe, category=Io`，未证明 CAS 断言；最近一轮在运行器启动前被 Windows 默认排除端口 58000 的检查拦截，未执行 E2E，故无新结论。需安全地为合成 fixture 选取允许端口后有界重跑。
- r3 真实备份/恢复 E2E 尚未执行到 fixture：runner-ancestor guard 在运行前拒绝；guard 已修复并经路径评审，真实包 E2E 仍待重跑。r1 历史备份恢复 PASS 不替代当前 r3/r4 证据。
- 干净 Windows x64 环境、原生 Avalonia 窗口、Explorer 托盘和多档 DPI 尚未验收；Headless UI Smoke 不能替代这些证据。P1 初始 schema 基线已有隔离 PG 证据，版本化 schema migrations 仍归 P2。保留 MIT 与 Docker 双路线。

## 2026-09-24 P1 当前状态 checkpoint

> 历史快照：以下内容记录 r3 冻结候选及本 checkpoint 后续审查修复之前的状态；若与上方最新 checkpoint 不一致，以最新记录为准。

- 当前不可公开发布，且当前源码尚未冻结为最终组合包。2026-09-23 的 r2 目录/ZIP/hash 只作为旧源码历史记录；r2 的两次 Host→PG 初始化/provision/Web/setup/正常停机周期通过，但不能代表当前源码包。r1 的真实 Host→PG 加密备份恢复 E2E 通过，也尚未在当前源码包复验。
- Chromium 143/r1200 与 ONNX notice 原文已补齐；104 项 notice collector/scanner fail-closed 定向检查通过。最终新 ZIP 尚未构建或扫描，故无当前发行 ZIP/hash/tag 可报告。
- 配对、当前用户和 AI 精简配置已有 App 生产接线与既有 Headless Smoke；AI 配置 CAS/去重及 TTL 修复的定向测试和审查通过。本地偏好 Headless 与 review 通过。未使用真实账号或付费 AI。
- Windows TCP control Bearer 归属 guard 的 fake 普通本机用户用例及审查通过，最终 Host E2E 未复验。Web 端口 Platform 安全 store/Host/gate 定向通过，App 已有生产接线；新增 fake seam 矩阵被测试进程文件锁阻断，未验收，也未跑真实端口切换。
- Launcher 恢复 UI 与托盘生命周期已有实现；当前证据限 Headless Smoke，Explorer 原生托盘/原生窗口未验。诊断事件枚举 Core/Exporter 定向通过，但预览和保存同字节的 UI 流程尚未接通。**此句为过时历史状态，已由本文件最新验收 checkpoint 更正：诊断生产 UI 接线及逐字节 Headless 验收已通过。**真实窗口 DPI/键盘与最低 Windows 版本仍未验。
- 最终真实 PG CAS probe 尚未 PASS；先前 harness `KeyError` 已修正，需重跑。最终源码包上的 Host→PG 备份恢复、Chromium 扫描、干净 Windows 验收均未完成。
- Docker Engine 已启动；隔离 Compose app+PostgreSQL health/login 本机烟测通过，测试未清理或改动原有容器卷。该烟测早于当前源码变更冻结，最终冻结后必须重跑 Docker 回归。
- 上述状态只更新进度，不代表完成发布。禁止把旧 r2 hash 当新包 hash，或据 Headless/fake 定向测试宣称原生/最终 Host E2E 通过。

## 2026-09-23 冻结源码组合包验收（r1，已由 r2 supersede）

- `restore_orchestration_luna` 于 2026-09-23 14:19 UTC 对 `PortableInstanceCatalog.cs`、`PortableRestoreSession.cs`、`PortableBusinessRestoreRunner.cs`、`WindowsPortableRestoreExecutor.cs`、`PortableBundleHost.cs`、对应 Windows Tests、备份契约文档与本日志发出 source-stop checkpoint；之后使用该冻结工作树打包。
- 最终冻结源码构建命令：`.\scripts\portable\build-launcher-prototype.ps1 -PythonIntegration`。0 warnings / 0 errors；Core19、HTTP契约3、Python14、Windows28、真实嵌入Python Maintenance/Normal启动重连停止2场景、UI Smoke、自包含 EXE 均通过。Launcher publish `launcher/dist/launcher-p1-acceptance-20260923-222124-dc264f0f989c/` 为227文件 / 214,081,209 B；bundle 使用另一轮 `-RealE2E`流水线的 Launcher 相同源二进制，路径 `launcher/dist/launcher-p1-acceptance-20260923-223410-4f103c36d312/`，227文件 / 214,081,209 B；.NET 10.0.12、win-x64。
- 完整目录生成命令：`python -B scripts/portable/build-portable-bundle.py --release-id acceptance-frozen-20260923-p1 --launcher-root launcher/dist/launcher-p1-acceptance-20260923-222124-dc264f0f989c`。目录 `launcher/dist/portable-acceptance-frozen-20260923-p1/` 共8,075文件（8,073 manifest entries + current + manifest），1,073,841,219 B；ZIP 411,834,889 B，SHA-256 `9b68a0e9b3f309508ceff7248498e9c4f70b29611edec89af76e0935c1a53f62`。`bundle-manifest.json` 1,845,809 B / SHA-256 `702caf167ea2053341ad42b2be454f34e9e88768414b10fe354f12107996baff`；`current.json` 957 B / SHA-256 `8c75ea6ff18d549908db4b41a7f8213bfe013988befde69607c514ffd92282f3`。文件内容、路径和 ZIP CRC 由 builder 校验。
- 发布前扫描命令：`python -B scripts/portable/scan-portable-release.py launcher/dist/portable-acceptance-frozen-20260923-p1`；结果 BLOCKED，唯一原因是 `avalonia-ui`、`chromium` 的第三方 notice gap及包内 gap 标记。扫描器通过全部未知/缺失/改写文件、Windows路径、hash、BOM、UTF-8、敏感文件、reparse、数据目录检查。已从 Python wheels 中央清单纳入60个锁定分布的93个 notice 文件。PG 17.11-3 锁定官方 ZIP 的 command-line notice 保持上游 legacy encoding，仅对精确 bundle path+SHA-256 `67181bbd5ddb5a0094aa9c82b97536a27811461a3b61c3c11588a2731cfc7b3b` 加例外，所有其他 notices 仍作严格 UTF-8 检查。
- 当前组合包 real E2E 命令：`.\scripts\portable\build-launcher-prototype.ps1 -PythonIntegration -RealE2E -BundleRoot .\launcher\dist\portable-acceptance-frozen-20260923-p1`。状态 `BUNDLE_ACCEPTANCE=PASS`：实际从此包校验 manifest/完整组件后运行两个新 Host→PG→provision→Web/setup→stop 周期。未创建账号、未触发抓取/AI/通知、未执行备份/恢复；结束后查询没有 `pg_ctl` / `postgres` 测试进程。早先同命令的相对路径解析失败已修复并重跑成功，不代表 PG token 阻断。
- Docker CLI 可调用，但 Docker API 的 `dockerDesktopLinuxEngine` named pipe 不存在，容器构建/启动未验；没有干净 Windows 安装环境，本机已有 .NET/Python 构建工具，故目标机清洁验收未做。不得因两个 Host/PG 测试周期成功宣称公开发行就绪：Avalonia 12.1.2 包内无 notice 原文、Chromium 143/r1200 缺 `chrome://credits` 可分发明细，仍有两项 license gap，现行 MIT 未更改。
- 暂留验收包目录（1,073,841,219 B）和 ZIP（411,834,889 B），以及先前并发源码时生成的 provisional 目录（1,073,201,330 B）和 ZIP（411,586,725 B，SHA-256见上文记录）；均仅供本地验收比对，保留至2026-09-30复核是否可清理。临时构建/测试路径只读盘点如下，均为本任务重建工件、没有业务数据；建立于 2026-09-23 UTC，清理期限 2026-09-30：
  - `F:\ai-goofish-monitor\github\ai-goofish-monitor-QB\.tmp\build\portable-acceptance\20260923-210213-d942f66b0ee6`，810,459,906 B，创建于 2026-09-23 13:02:13 UTC，首轮构建工件。
  - `F:\ai-goofish-monitor\github\ai-goofish-monitor-QB\.tmp\tests\portable-acceptance\20260923-210213-d942f66b0ee6`，160,138 B，创建于 2026-09-23 13:03:01 UTC，首轮 Smoke 图片/日志。
  - `F:\ai-goofish-monitor\github\ai-goofish-monitor-QB\.tmp\build\portable-acceptance\20260923-222124-dc264f0f989c`，811,186,055 B，创建于 2026-09-23 14:21:24 UTC，冻结源码 full Release 构建工件。
  - `F:\ai-goofish-monitor\github\ai-goofish-monitor-QB\.tmp\tests\portable-acceptance\20260923-222124-dc264f0f989c`，164,853 B，创建于 2026-09-23 14:21:39 UTC，冻结源码 Smoke 图片/日志。
  - `F:\ai-goofish-monitor\github\ai-goofish-monitor-QB\.tmp\build\portable-acceptance\20260923-223205-eec931898a51`，730,732,369 B，创建于 2026-09-23 14:32:05 UTC，首次 E2E 命令路径解析失败后的构建工件。
  - `F:\ai-goofish-monitor\github\ai-goofish-monitor-QB\.tmp\tests\portable-acceptance\20260923-223205-eec931898a51`，空目录，创建于 2026-09-23 14:32:21 UTC，首次 E2E 失败时创建。
  - `F:\ai-goofish-monitor\github\ai-goofish-monitor-QB\.tmp\build\portable-acceptance\20260923-223410-4f103c36d312`，811,186,055 B，创建于 2026-09-23 14:34:10 UTC，最终 E2E 成功轮的构建工件。
  - `F:\ai-goofish-monitor\github\ai-goofish-monitor-QB\.tmp\tests\portable-acceptance\20260923-223410-4f103c36d312`，164,334 B，创建于 2026-09-23 14:34:25 UTC，最终 E2E 成功轮 Smoke 图片/日志。
- 列举构建与测试进程后没有 dotnet、Launcher、postgres 或 pg_ctl 运行；未发现这些目标自身或后代 reparse point。清理这些 exact targets 的递归删除在 auto-review 被拒绝（要求取得确认后才能清理多个暂存构建/测试目录），没有再次尝试；请总任务后续向用户呈列路径并取得确认，或至少于 2026-09-30复核后清理。

## 2026-09-23 最终 r2 包与复跑记录

- License notice inventory 冻结后，Avalonia 12.1.2 官方 MIT 原文以唯一精确组件/版本/路径/hash allow-list（SHA-256 `213814d306090074d234d760239ff0f67eb9b8d20eefb4d5631bb39dbe0b769b`）接入包；Python 60 distribution 的93个notice全部清单化；Chromium 143/r1200 的 `chrome://credits` 导出失败，仍保留唯一 `is_gap=true`。builder定向测试6/6。
- 发布扫描器针对官方 Postgres 17.11-3 ZIP 内的 `commandlinetools_3rd_party_licenses.txt` 使用唯一精确 bundle path + pinned SHA256 `67181bbd5ddb5a0094aa9c82b97536a27811461a3b61c3c11588a2731cfc7b3b` 放行 legacy encoding；BOM仍扫描，其他 notices仍必须UTF-8。扫描器测试10/10。
- 为 UI Smoke 的 framework-dependent `dotnet Ui.Smoke.dll` watchdog 子进程修正调用参数：先传当前入口 assembly，再传 child args；自包含 apphost不插 assembly。定向 `--child-invocation-self-test` PASS，覆盖 framework-dependent/apphost/缺程序集fail-closed；watchdog timeout cleanup PASS。发现 UI lock 缺 `win-x64` target，使用现有 `-RefreshLock` 仅刷新 App/Smoke RID锁，无 package reference 版本变化/新增依赖，随后 locked restore通过。
- 最终 Launcher full build + Host E2E命令：`$env:AVALONIA_TELEMETRY_OPTOUT='1'; .\scripts\portable\build-launcher-prototype.ps1 -PythonIntegration -RealE2E -BundleRoot .\launcher\dist\portable-acceptance-frozen-20260923-p1-r2`。Release 0 warnings / 0 errors；Core19、HTTP契约3、Python生命周期14、Windows28、真实Python Maintenance/Normal 2场景、UI Smoke、自包含验证、bundle manifest/Host→PG启动/provision/Web/setup/stop 两周期均通过。输出 `BUNDLE_ACCEPTANCE=PASS`。最终 launcher publish 为 `launcher/dist/launcher-p1-acceptance-20260923-232959-f30a988c384e/`（227 files / 214,081,209 B；.NET 10.0.12 x64）。
- Windows 的 `TestCancelledStartAsync` 有一次 full suite 1/28 失败（取消后子进程不能以常规 pipe stop）；根因未确定。随后同构建输出精确隔离 `--test '启动取消仍保留归属'` 单次 PASS，最终 full locked build 再次28/28 PASS。没有生产代码修复，故记录为间歇/未解释现象，不称flaky已修或回归关闭。
- 最新完整组合包命令：`python -B scripts/portable/build-portable-bundle.py --release-id acceptance-frozen-20260923-p1-r2 --launcher-root launcher/dist/launcher-p1-acceptance-20260923-223410-4f103c36d312`。冻结源码目录 `launcher/dist/portable-acceptance-frozen-20260923-p1-r2/` 共8,076文件，1,073,842,659 B；manifest有8,074 entries。ZIP `launcher/dist/portable-acceptance-frozen-20260923-p1-r2.zip` 411,835,807 B / SHA-256 `32b07aeaf51322db52be9b2ce0a7a4fdc37b175c57a30ada1f01ec48893d3b27`。`bundle-manifest.json` 1,845,999 B / SHA256 `4475241cdacecba6fdd85e0d1eeb72054d5a1506475020ca5ad84c3d19eafdd4`；`current.json` 960 B / SHA256 `65016f3de9d66fe1c7dfc691f98fac21f56e7e2d2dcd84053661b5de731fd868`。源文件/路径 hash、manifest完整集合、ZIP CRC均通过 builder 校验。
- 最终扫描命令 `python -B scripts/portable/scan-portable-release.py launcher/dist/portable-acceptance-frozen-20260923-p1-r2` 返回 BLOCKED，仅为 `chromium` notice gap 与 `THIRD_PARTY_LICENSE_GAPS.md` marker；无未知/缺失/改写文件、无BOM/UTF8问题、无敏感文件/数据目录/reparse。不可标为公开发行放行。
- 已查询没有 dotnet、Launcher、postgres 或 pg_ctl 运行。r2 的两周期 E2E不包含业务备份/恢复。独立 `BusinessBackupRestoreAcceptance` 有专属负责人和固定 r1 release-id，本次未运行；需由该负责人有界适配到最终 release-id 后再演练，不能把缺口描述为通过。Dockerfile/compose 语法 `docker compose ... config -q` 2项PASS，legacy数据路径/导入测试7项PASS；Docker engine pipe不存在所以容器构建/PG/业务入口未验证；没有可用干净 Windows 无工具机器。
- 新增后续 `.tmp` 路径继续归本任务且重建可得，保留至2026-09-30复核：
  - `F:\ai-goofish-monitor\github\ai-goofish-monitor-QB\.tmp\build\portable-acceptance\20260923-230122-7cc880e479de`（730,744,283 B，创建 2026-09-23 15:01:22 UTC，RefreshLock build）；相同 ID 的 `F:\ai-goofish-monitor\github\ai-goofish-monitor-QB\.tmp\tests\portable-acceptance\20260923-230122-7cc880e479de` 是0文件空目录，15:01:42 UTC创建。
  - `F:\ai-goofish-monitor\github\ai-goofish-monitor-QB\.tmp\build\portable-acceptance\20260923-230428-9a5ab883d1f2`（729,672,848 B，创建 2026-09-23 15:04:28 UTC，首次 Python E2E类未闭合导致 solution build失败；后用于 Smoke定向编译；没有对应 test-root 目录）。
  - `F:\ai-goofish-monitor\github\ai-goofish-monitor-QB\.tmp\build\portable-acceptance\20260923-232115-e765c6806fdb`（730,877,945 B，创建 2026-09-23 15:21:16 UTC，Windows full suite 一次失败后 exact-case 隔离复跑）；相同 ID 的 `F:\ai-goofish-monitor\github\ai-goofish-monitor-QB\.tmp\tests\portable-acceptance\20260923-232115-e765c6806fdb` 是0文件空目录，15:21:31 UTC创建。
  - `F:\ai-goofish-monitor\github\ai-goofish-monitor-QB\.tmp\build\portable-acceptance\20260923-232959-f30a988c384e`（811,339,359 B，创建 2026-09-23 15:29:59 UTC，最终full build/E2E）；相同 ID 的 `F:\ai-goofish-monitor\github\ai-goofish-monitor-QB\.tmp\tests\portable-acceptance\20260923-232959-f30a988c384e`（4 files/165,412 B，15:30:22 UTC创建）保留Smoke PNG和运行摘要。
  - `F:\ai-goofish-monitor\github\ai-goofish-monitor-QB\.tmp\tests\portable-acceptance\windows-cancel-start-isolated-20260923`（0 files/0 B，创建 2026-09-23 15:29:04 UTC，精确单项Process test root）。所列target均无reparse point。结束时有另一个运行中的dotnet进程且CIM commandline拒绝访问，无法证明上述所有build root皆未占用；不清理。此次也未再次尝试自动审批已拒绝的多目录递归删除；如需清理须用户确认。

## 2026-09-23 最终 r2 locked build 与 Host E2E

- App/Smoke locks 唯一漂移为 UI Smoke 锁缺少 `net10.0-windows7.0/win-x64` target；运行 `scripts/portable/build-launcher-prototype.ps1 -RefreshLock` 后只增加当前已有 Avalonia 依赖的 RID graph，无新 PackageReference、无版本变化。随后 locked-mode full build通过。
- 为 framework-dependent Headless watchdog 子进程增加精确启动参数分支：`dotnet Ui.Smoke.dll` 先传当前入口 assembly，再传 child args；自包含 apphost不插 assembly，缺路径拒绝启动。`--child-invocation-self-test`专项通过，覆盖framework-dependent/apphost/缺程序集fail-closed；watchdog timeout子进程回收PASS。
- 最终全套命令：`$env:AVALONIA_TELEMETRY_OPTOUT='1'; .\scripts\portable\build-launcher-prototype.ps1 -PythonIntegration -RealE2E -BundleRoot .\launcher\dist\portable-acceptance-frozen-20260923-p1-r2`。0 warnings/0 errors；Core19、HTTP3、Python14、Windows28、真实Python Maintenance/Normal两场景、自包含包、UI Smoke全部通过。`TestCancelledStartAsync` 曾在前一完整批次出现1/28失败；exact-case隔离复跑1/1及最终full 28/28均过。无生产代码修复，不据此称间歇问题已修。
- 同一命令对最终 r2 包 `BUNDLE_ACCEPTANCE=PASS`：package manifest和组件验证后运行两个真实 PG initdb/provision/Web/setup/正常停止周期。没有真实账号、抓取、AI、通知、备份或恢复；本轮启动 E2E 结束后查询无 dotnet、Launcher、postgres或pg_ctl进程。BusinessBackupRestoreAcceptance由其专属代理运行；不把其r1 fixture测试混称为r2实际备份/恢复通过。
- 最终目录 `launcher/dist/portable-acceptance-frozen-20260923-p1-r2/`：8,076 files / 1,073,842,659 B；manifest 8,074 entries。ZIP `launcher/dist/portable-acceptance-frozen-20260923-p1-r2.zip` 411,835,807 B / SHA-256 `32b07aeaf51322db52be9b2ce0a7a4fdc37b175c57a30ada1f01ec48893d3b27`。Manifest 1,845,999 B / SHA-256 `4475241cdacecba6fdd85e0d1eeb72054d5a1506475020ca5ad84c3d19eafdd4`；current 960 B / SHA-256 `65016f3de9d66fe1c7dfc691f98fac21f56e7e2d2dcd84053661b5de731fd868`。builder核验源hash、路径、manifest完整集合和ZIP CRC。
- 最终扫描唯一 findings 为 `license-gap: chromium` 和对应 `THIRD_PARTY_LICENSE_GAPS.md` marker；扫描器10/10、bundle tests6/6。Chromium 143/r1200 credits notice gap未关，状态不能标公开发行放行。Docker Compose 语法两项PASS，便携数据路径/导入测试7项PASS，但Docker engine pipe不存在，故容器构建/挂载/PG/业务入口仍未验；无干净 Windows 无工具环境。真实备份/恢复E2E未由发行验收代理运行。

## 2026-09-23 P1 恢复编排 checkpoint（GPT-6 Luna）

- 新增 `PortableInstanceCatalog`：旧 `data` 根在无活动指针时仍是活动实例；隔离恢复候选只在稳定容器的 `data/instances/restore-<随机ID>` 新建并取得独立 lease。端口随目标 metadata 保存。活动选择在 `data/launcher/active-instance.json`，有独占提交锁、revision CAS、相对路径/重解析点/lease ID/目标 metadata 验证及同卷原子替换；只有 `Validated` 目标能提交。
- `RealPortableStackHost` 按活动指针解析数据根和端口，启动前复核 pointer revision 与 lease；普通启动仍使用原有显式手动启动流程。新增 `CreateBusinessRestoreSession` 生产工厂，没有新增恢复 UI。
- 新增固定 `PortableBusinessRestoreRunner` 和 `WindowsPortableRestoreExecutor`：新 PG17 initdb → owned PG 启动 → 私有 stdio `restore`、四次 proof → handoff 通过目标用户 DPAPI 导入/读回/清理 → Maintenance-only Python readiness/schema 检查 → PG smart stop → 记录恢复 schema 完成。Secrets/DSN/passphrase 不进入 argv、环境或日志。任何失败保留目标；归属未知时 executor 保留组件引用、lease 和运行身份，不再允许接管。
- `ConfirmActivationAsync` 要求独立的备份来源信任确认及切换确认文字。切换前再次确保目标停止；旧活动实例通过实例 lease、PG/Python runtime 进程身份审计，Running/Unknown/身份缺失/DPAPI 缺失/路径或锁不可读均拒绝。旧根 lease guard 保持到 active pointer CAS 提交完成，避免 pointer 写入竞态；即使确认成功也不自动启动 Web/worker，需常规 Launcher 手动启动。
- 恢复预览包含 source/target instance ID、备份 SHA、逐表行数、恢复文件数、撤销会话数和备份时点损失提示。现有 restore helper 未将 `created_at` 与恢复文件总字节数交回 C#，两项如需显示必须标未知，不能显示伪零值。恢复前要求用户确认信任备份来源，因为数据库 dump 可执行 SQL。
- 验证命令：锁定 .NET 10.0.401 下 `dotnet restore launcher/tests/AiGoofish.Launcher.Platform.Windows.Tests/AiGoofish.Launcher.Platform.Windows.Tests.csproj --configfile launcher/NuGet.Config --locked-mode --artifacts-path .tmp/build/portable-restore-luna-20260923-phase1 --property:NuGetAudit=false --property:RestoreAdditionalProjectFallbackFolders=<.tmp/dependencies/p0-b1/nuget-packages>`，随后同项目 `dotnet build --configuration Release --no-restore --artifacts-path .tmp/build/portable-restore-luna-20260923-phase1 --property:UseSharedCompilation=false`：0 warning / 0 error；`AiGoofish.Launcher.Platform.Windows.Tests` 28/28 PASS；`python -B -m unittest tests.test_portable_restore_entry -v` 4/4 PASS。Windows harness 覆盖 fake 私有 stdio helper、四次 proof、argv/env 秘密排除、坏 proof、来源仍运行拒绝切换、source 停止后确认切换、CAS/旧数据保留/失败目标保留。
- 未启动 PG、未对真实业务数据执行恢复，也未运行真实抓取/AI/通知。一次全 solution restore 因沙箱无法访问 `api.nuget.org:443`（NU1301）未完成；Windows Platform + 专属测试项目本地锁定 restore/build 成功。真实 Host→PG 恢复 E2E、PG17 实际 initdb/restore、恢复 UI/差异界面及 source 进程 crash 遗留场景未验，不能把 P1 完整恢复勾为通过。

## 2026-09-23 P1 运行状态/关闭/诊断切片

- Core 新增串行只读运行状态采样，快照时间随采样更新；运行期受管组件若不再 Running，则状态转为明确异常并保留现场，不自动重启或结束进程。UI 首次就绪后采样、每30秒更新，并展示采样时间/30秒新鲜阈值、本 Launcher 进程工作集与其所在卷可用空间。非运行实例不展示全机或 Web 业务数据摘要；BackingUp/Stopping持操作门时保留旧观测时间，UI据此可看出过期。
- 关闭窗口：有活跃组件时首次给出“后台运行（最小化到任务栏）/安全退出/取消”，支持记忆；记忆保存在 `%LOCALAPPDATA%/AiGoofishLauncher/close-choice.txt`。没有实现系统托盘，所有文案不宣称托盘。安全退出等待60秒后提供继续等待/取消退出，取消停止请求不会强杀进程；关闭失败则保留窗口。当前 UI Smoke 仅演练选择对话框，原生最小化恢复/实际长任务退出未验。
- 诊断摘要仅导出固定字段：Launcher版本、Windows/x64平台、总体状态、固定组件ID和状态、采样时刻；白名单排除任意消息、观察错误、路径、日志、配置、账号、凭据、请求内容与业务数据。导出前展示准确 JSON 预览，需选本地目录；最大16 KiB，以同目录唯一临时文件写入/刷盘后原子移动，已存在目标拒绝覆盖，失败仅提示通用信息。未添加依赖或上传。
- 验收命令：`powershell -NoProfile -ExecutionPolicy Bypass -File scripts/portable/build-launcher-prototype.ps1`；Release全方案构建0警告/0错误，Core19、Python14、Windows22通过，离屏UI Smoke PASS，自包含win-x64包加载.NET10.0.12通过（227文件/214,017,677字节）。另以构建产物运行 `AiGoofish.Launcher.Ui.Smoke.dll --diagnostic-exporter-acceptance`，检查白名单/敏感值排除、16KiB限额、无覆盖、原子文件和失败无临时残留，PASS。
- UI验证边界：关闭对话框和现有页面通过Headless UI Smoke；该结果不是Win32原生/DPI、系统托盘、原生窗口恢复、真实PG/Web故障注入或干净Windows验收。最终构建产物 `.tmp/build/portable-acceptance/20260923-211719-a38b0b4d9680/`（528文件，810,659,200字节，可重建；留作本轮验收复核，2026-09-30清理）；UI验收快照 `.tmp/tests/portable-acceptance/20260923-211719-a38b0b4d9680/`（4文件，164,301字节，PNG/运行摘要，2026-09-30清理）。首次构建目录/快照共810,807,912字节已按准确路径清理。自包含发布目录位于 `launcher/dist/launcher-p1-acceptance-20260923-211719-a38b0b4d9680/`（227文件，214,017,677字节；复用验收产物，不是组合便携ZIP）。

## 2026-09-23 P1 发布验收基础设施

- `scripts/portable/build-launcher-prototype.ps1` 每次使用唯一 `.tmp/build/portable-acceptance/<id>/artifacts` 和 `launcher/dist/launcher-p1-acceptance-<id>`，不复用历史 bin/publish 文件；Core、Windows 进程和 PostgreSQL 测试 DLL 只从本轮 Release 输出精确选择。默认流程现覆盖 Core、HTTP、Python 生命周期、Windows 平台、离屏 UI、自包含入口；PG 集成保持显式 `-PostgresIntegration`，完整 Host→PG 可选验收使用 `-RealE2E -BundleRoot <目录>`。
- `build-portable-bundle.py --launcher-root <新发布目录>` 可将本轮自包含 Launcher 输入组合进完整 Windows x64 目录，且输出拒绝覆盖；构建器持续核验组件锁、源 hash、完整 manifest 和 ZIP CRC。最终可接受包须在并行 Launcher 源码冻结后重新构建，防止与正在进行的恢复/状态改动竞争。
- 新增 `scan-portable-release.py` 对目录逐项核验未知/缺失/改写文件、manifest 路径和大小写冲突、BOM、敏感文件、用户数据目录、reparse point 和第三方许可 gap；遇到许可 gap 以 BLOCKED 返回，不代表 MIT 项目许可已改变。`test_scan_portable_release.py` 8/8，原 bundle 测试 `tests/test_portable_bundle.py` 5/5。
- Launcher 脚本语法检查通过。当前源码一次完整 `-PythonIntegration` 运行：Release 编译 0 warning/0 error；Core18、HTTP契约3、Python生命周期14、Windows22、真实 Python Maintenance/Normal 启停重连2场景、UI Smoke 均通过；自包含 EXE 报告 .NET 10.0.12 x64，发布目录227文件 / 213,986,885 B。
- 因在收到并行源码冻结提醒前 bundle 命令已启动，产生临时验收目录 `launcher/dist/portable-acceptance-20260923-p1/`（7,982 文件含 current/manifest，1,073,201,330 B）及 ZIP（411,586,725 B，SHA-256 `9c11be203b9e8667a4c75adb9cbaf883eaa98648d197a4cba74a92f2c0102d48`）。该产物明确是**未冻结中间件，不作当前源码/最终验收证据**；等待两个并行 Launcher 源码代理冻结后比对源身份，再以新 release id 重建。目录、ZIP 和本任务 build artifacts 暂保留，不清理；到期由总任务收尾时复核用途/引用后处理。
- 只读扫描上述中间目录返回 BLOCKED：扫描命中文本BOM，且另有修复代理记录共21个发行文件已只去除UTF-8 BOM、其余字节一致，源侧验证21/21；尚需冻结后重打包复扫。许可缺口为 Avalonia、锁定 Python wheels、Chromium；并保留 `THIRD_PARTY_LICENSE_GAPS.md` 阻断标记。真实 Host→PG E2E 未对该未冻结目录运行；当前 sandbox 之前的复跑曾因 `pg_ctl` restricted-token Win32 87 被阻断，需在可创建该 token 的普通 Windows 用户环境复跑。Docker CLI 存在但 `dockerDesktopLinuxEngine` pipe 不存在；未启动引擎/拉镜像。干净 Windows 无开发工具环境也未提供。
- 许可缺口细目（缓存只读核对，未下载或做法律结论）：Avalonia/Avalonia.Desktop 12.1.2 的 `avalonia.nuspec` 声明 MIT expression 与 SPDX license URL，但缓存包中没有 LICENSE/notice 文本；官方精确 commit 的 MIT 原文已在 `scripts/portable/third-party-notices/avalonia-MIT.txt` 留证，不过当前 bundle collector 不接受该仓库内素材作为 notice source，inventory 仍保留 gap。CPython 3.13.15 的60个锁定 distribution 已逐项匹配并将93个缓存 license/notice 文件纳入中央清单；`METADATA` 的 License-File 引用均有对应文件，wheel aggregate gap 可在最终包复扫确认后关闭。Chromium 143.0.7499.4 / Playwright r1200 的 `chrome-win64/ABOUT` 只指向运行时 `chrome://credits`，发行树没有 third-party credits 文本，继续标 gap。

## 2026-09-23 续办 checkpoint

- 便携 AI 配置：AI/代理保存共用 PostgreSQL 事务内 `config_revision` CAS；主审发现删除重建后 revision 撞号，已补 `config_id` 身份 CAS，旧行或旧 revision 返回 409。空 key 保持、明确移除和 Base URL 主机变更保护。定向 `tests.test_portable_ai_config_revision` 9/9 通过；未跑真实 PG 并发 CAS。
- 便携 AI 手动测试：新增显式确认、稳定 request ID 去重、每用户限频、固定非敏感文本、单次 HTTP、`httpx` retries=0、无重定向、超时结果标记未知、不写正式健康缓存和目标主机密钥解绑；主审补有界 DNS 解析与已验证 IP 固定连接、关闭便携旧多探测路由、修前端结果变量作用域。`tests.test_portable_ai_manual_test` 9/9 通过；未调用真实 AI/网络。
- 最终审查补出 HTTP 取消竞态：已用 `asyncio.shield` 和完成回调让供应商单次任务独立缓存，重试同一 request ID 不会再次发出请求；取消状态返回未知。便携 AI 手动测试现 10/10，完整便携定向集 145/145 通过。
- 备份恢复：解密 ZIP 清理失败现在阻止恢复发布，新增故障注入测试；备份归档/业务备份/恢复定向合计 26/26 通过。真实隔离 PG17 恢复证据仍以此前 PASS 为准；本轮复跑被沙箱 `pg_ctl: could not create restricted token: error code 87` 阻断。
- Host 恢复：跨进程 Host-only 接管、同 PID/start/exe 和 setup token 核对、正常 Stop 通过；Headless `Window.Show()` 后 UI pump 卡住，不能标 UI PASS。诊断 fixture `F:\ai-goofish-monitor\github\ai-goofish-monitor-QB\.tmp\tests\host-recovery\88057d8429ec41f9b592927f740834de`，50,615,645 B，保留至 2026-09-30 复核。
- 本轮未删除历史失败 fixture，未触发真实抓取、AI、通知或账号操作；`node --check`、`git diff --check` 通过。`compileall` 因已有 `src/web/__pycache__` 文件访问冲突未作为通过证据，Python 测试导入已成功。
- 主代理再次运行隔离 PG 恢复命令，在 `pg_ctl` 启动前因 sandbox restricted token 87 失败；新增诊断目录 `F:\ai-goofish-monitor\github\ai-goofish-monitor-QB\.tmp\tests\portable-pg\集成 冒烟-schema-0asnn4tr`。当前共 9 个 `集成 冒烟-schema-*` 失败目录；未发现匹配的 postgres 进程，私有 ACL 使可靠容量盘点受阻，不删、不重复建 fixture。尝试普通 Windows 用户上下文执行时，自动审批服务返回 503，命令未执行；不绕过审批。
- Host 全停重启：保持 `TryRecoverExistingAsync` 被动核验不自动启动，新增显式 `StartStoppedExistingAsync`，UI 的手动“一键启动”对已停旧实例调用它；首次新实例仍用普通 Coordinator.Start。PG 缺运行记录但已有初始化标记时改判 Unknown 而非误判 Stopped。Release build 0 warning/error、Core 15/15；真实 PG/UI 重启仍待验，不与 Headless 窗口恢复混称。
- 主代理跑 `python -B -m unittest discover -s tests -p 'test_portable_*.py' -q` 初次 141 项出现 1 个隔离导入回归：顶层 `sqlalchemy.text` 令模拟 SQLAlchemy 的旧测试导入失败。将 `text` 改为事务写方法内延迟导入后，完整 141/141 PASS；无真实业务调用。前端两个模块 `node --check` 和 Python 变更文件无写入语法检查通过。
- Launcher 备份切片：新增 Host `CreateBusinessBackupAndStopAsync`、Core 停写编排和固定 Python `portable_backup.py` 入口；实例锁/操作门独占，Web 正常排空后 PG 保持归属运行，后端三次停写回调经 stdio 要求 Launcher 复核，失败不报告成功；结束受控停 PG。口令/keys/DSN 不入 argv、环境、日志，但既有 `backup_business.py` 会在当前用户私有 ACL staging 暂存明文 `recovery-keys.json`，加密后清理，不能宣称全程无临时明文。Core 18/18、Windows 20/20、Python 定向 13/13、全方案编译 0 warning/error；主代理完整便携 Python 定向集 144/144 PASS。无备份 UI、无真实 Host→PG 备份演练；完整构建脚本遇 UI Smoke DLL 入口计数 2 vs 1，尚未放行。
- 备份 UI 已接线：模拟模式禁用真实备份按钮；真实模式可选数据根外目录、输入两次遮罩口令，明确停 Web/PG、耗时和忘记口令风险；成功只显示 Host 返回的路径与 SHA，失败不显示异常详情，备份中禁用启动/停止。修正 Avalonia `TextBox.Watermark` obsolete 为 `PlaceholderText`，并让构建脚本只选 release UI Smoke DLL，避免历史 debug 输出造成假失败。隔离 .NET10 完整构建与验收通过：Core18、Python14、Windows20、UI_SMOKE_PASS、发布目录 227 文件/213,962,861B，0 warning/error。未运行真实备份。

## 授权和续办

- 2026-09-12 用户要求离开期间推进至可验收；已批准既定运行/构建依赖，取消本任务容量上限。
- 仍保留低磁盘（10 GiB 或 5%）暂停、真实数据保护、MIT、Docker 保留，以及不擅改抓取/AI/通知行为。
- 主代理统筹和审查，Sol xhigh 执行有界实现；仅在任务不重叠且有实际收益时同时推进两个工作面。不得复制全仓或重复扫描。
- 当前持久目标为本任务的便携落地目标。续办自动化 ID：`launcher`，每小时检查；正在推进不重复派工，完成或只能等待用户决策时暂停续办。
- 未授权购买、外部发布、永久发行签名身份、真实平台账号操作或真实付费测试。此类发布门槛单列，不阻塞可安全独立完成的开发。

## 已有证据

- P0-A 路径基础：主代理复跑 6/6 通过；未接线既有业务。
- P0-B1 Core：主代理复跑 12/12、0 warning/0 error，脚本环境恢复通过。
- P0-B1 UI：依赖锁生成；SDK 8.0.406 Roslyn 4.11 不满足 Avalonia 12.1.2 的 4.14 生成器要求，未放行 UI。
- P0-B1 缓存约 1.42 GiB，保留复用；编译器占用残留未强删。明细见 README。

## 当前批次

| 批次 | 实现归属 | 文件范围 | 目标与验收 | 状态 |
| --- | --- | --- | --- | --- |
| P0-B2 工具链与窗口 | `p0_launcher` | `launcher/`、`scripts/portable/` | F 盘隔离 .NET 10、哈希验证、完整构建与 Core 测试、win-x64 自包含目录、离屏渲染/冒烟证据 | 主代理完整锁定脚本复跑通过；Core 12/12、UI 演练/关闭/两图、发布 EXE 验证通过 |
| 维护后端入口 | `portable_maintenance` | `portable_server.py`、`src/portable/`、定向测试与接口文档 | 不加载业务的维护入口、令牌保护的实例/DB/schema 状态、无初始化/调度/外部副作用 | 主代理复跑 22/22 通过，未连接真实 PG |
| 隔离 PG 集成冒烟 | `portable_maintenance` | `scripts/portable/prepare-postgres.*`、`tests/portable_pg_smoke.py` | 官方二进制来源/校验、全新隔离集群、密码认证与低权限探测、schema 缺失/就绪、受控关闭 | 主代理真实命令复跑 PASS：17.11-r3、UTF8/C、SCRAM、HTTP 401/401/503/200，集群已停且清理；仍使用系统 Python 验证 |
| P0-B3 进程与实例安全层 | `p0_launcher` | Launcher Windows adapter 及进程测试 | 排他实例锁、参数/环境与日志脱敏、真实测试子进程归属、正常停止与超时保留状态 | 主代理完整脚本复跑通过：Windows 9/9、Core 12/12、UI/发布回归；未接默认 UI 或 PG |
| 内置 Python | `python_runtime_resume`（Terra high 接续） | Python 组件锁、依赖锁、bootstrap 与准备/验证脚本 | 官方 CPython 3.13.15 embeddable、隔离 site-packages、固定入口、内置解释器真实验证 | 身份缓存缺口修复；主代理 prepare -Offline 校验通过，内置 HTTP 真实 PG 冒烟及鉴权正常退出通过；完整业务导入仍待验 |
| 便携配置与初始化隔离 | `portable_config`（Terra high） | `src/portable/context.py`、config/storage 最小分支与测试 | 不发现项目 .env、受控 DB/密钥来源、非敏感偏好白名单、普通连接不建表/默认管理员、不用默认加密密钥 | 主代理复跑 37/37（配置 9、路径 6、维护 22）通过；配置用 subprocess/mock 隔离，完整 Web 与 Docker 实际运行未验证 |
| P0-B4 实例密钥 | `p0_launcher`（Sol xhigh） | Windows DPAPI 存储与定向测试 | 当前用户保护、实例 ID 绑定、显式 CreateNew/Load、不覆盖非空/未知实例 | 主审反馈已修；主代理完整脚本复跑通过：Windows 15/15、Core 12/12、UI/发布回归，仍无跨机器密钥恢复 |
| P1 初始 schema 基线 | 主代理接续 `schema_resume`（Terra high） | 新 schema 服务、显式入口、专属单元/集成测试 | PG 事务和独占锁下仅初始化空 schema、版本最后提交、角色最小权限、无默认账号；显式首管理员事务服务 | 两代理 429 后主代理修测试语法与卫生，独立真实 PG PASS；补验建表后失败全回滚、空表双请求仅一个管理员通过；种子、嵌入解释器CLI及完整Web未验 |
| P1 文件路径接线 | `portable_paths_wiring`（Terra high） | app_paths、用户文件/账号/头像/日志/结果/指南/worker消费者及专属测试 | 便携程序只读、持久文件进 data；旧版路径保持；无真实业务运行 | 两批代码已接线；主审修 browser 独立组件布局/worker cache及控制凭据继承后，worker+路径+schema 13/13复跑通过；真实抓取不在测试授权内 |
| P0-B5 真实PG适配 | `p0_launcher`（Sol xhigh） | C# WindowsPostgresComponent与专属集成测试 | 空目录SCRAM初始化、直接postgres长进程、归属记录/重连、smart正常停止 | 主代理独立完整script -PostgresIntegration通过：PG5/5、Core12/12、Windows15/15、UI/自包含回归；pg_isready仅传输可用 |
| P1 可撤销用户会话 | 主代理 | portable/sessions、auth、storage最小便携分支与测试 | opaque随机会话只存哈希、权限读取当前用户、登出/改密/停用撤销；legacy签名会话不变 | 隔离及真实app-role存储会话CRUD通过；主代理嵌入Python复跑4项auth含真实Web导入/静态挂载/401测试通过，未执行业务lifespan |
| P1 正常Web/首次引导 | `portable_config`（Terra high） | portable_web、web_runtime、main便携生命周期门控与测试 | app/probe同数据库独立角色、一次性setup与control分权、首次账号前阻断业务、无隐式维护副作用 | 主审修正后Web+auth9/9通过；真实PG+真实router完成setup/login/logout/reopen gate通过；目前setup仅JSON接口无页面，安全排空停机接续实现中 |
| P1 新实例业务DB准备 | 主代理 | portable/provision、portable_provision入口及PG集成fixture | C# marker与实际PGDATA匹配、独占锁、独立app/probe角色/库/schema；已有状态拒绝覆盖 | 真实隔离PG准备+重复调用不改数据PASS，已交B6接入；非事务建库中断只拒绝自动恢复，需后续诊断恢复流程 |
| P0-B6 Python真实栈 | 主代理接续`p0_launcher` | C# Python HTTP组件及专属真实PG/Python集成 | 显式嵌入解释器、鉴权身份ready、维护/正常Web正常stop、角色准备入口 | 主代理-PythonIntegration真实栈PASS：PG初始化/provision→Maintenance ready/stop→Normal ready/stop→PG smart stop，0warning/error+Core12/Windows15/UI发布回归通过；默认窗口仍模拟，crash重连凭据未完成 |
| P1 首次设置页面 | 主代理 | templates/portable_setup.html、static/portable/setup.css/js、UI回归脚本 | 清晰中文表单、字段错误/摘要聚焦、提交去重、未知结果不重发、秘密不落浏览器存储 | 嵌入Python+锁定Chromium本地拦截fixture交互PASS，1120/375截图主代理已查看；真实setup路由资源集成待最终回归 |
| P1 停机排空 | `portable_config`（Terra high） | Web runtime coordinator、main和后台协程最小追踪 | 拒新业务、等待请求/BackgroundTasks/worker/调度在途、安全回调退出；超时取消恢复不强杀 | 主审要求修首次引导control被拦、Stopped重新接单、ASGI入场竞态/后台monitor与scheduled job漏跟踪，仍未放行 |
| 组合包构建基础 | `python_runtime_resume`（Terra high） | 新build-portable-bundle与布局契约、测试 | 精确白名单/组件hash/无用户数据，dry-run核算磁盘峰值 | 执行中，先dry-run；与B6商定current.json后才构建，不把模拟UI包标正式 |
| B7真实窗口接线 | `p0_launcher`（Sol xhigh） | App与Windows组合host新类、UI测试 | current.json+manifest验证、版本组件路径、手动启动PG/provision/normalWeb、setup码显示、正常停止 | 原代理接续执行中，B6后端接口为主代理已复核版本 |
| P1固定默认资源 | `portable_paths_wiring`（Terra high） | portable/seeds与schema/provision显式事务接线、定向tests | 两个git HEAD默认资源+seed_manifest严格校验，不扫描用户目录、不覆盖已有资源 | 执行中，真实C#fixture已准备同格式默认资源；主代理bundle生成manifest |
| 发行第三方通知盘点 | `portable_paths_wiring`（Terra high） | 固定notices素材与机器可读inventory、说明 | 复用已缓存准确版本的许可/版权通知，缺失明确标gap，不擅改MIT | 执行中，不等于法律许可结论或公开发行放行 |
| P1加密备份容器核心 | `portable_config`（Terra high） | 新backup_archive库与专属测试 | 流式AEAD、密码KDF、完整manifest、全新隔离恢复、安全解包 | 已派工；仅容器，PG一致性dump/DPAPI恢复密钥/UI集成另批次，不能标完整备份能力 |
| 配套Chromium组件 | `python_runtime_resume`（Terra high） | browser runtime锁、准备/校验脚本及受控浏览器测试 | Playwright1.57/Chromium1200完整离线组件，安全提取/manifest，不调用真实业务 | 主代理prepare-browser.ps1 -Offline -Smoke复跑PASS、4项单测PASS；308个archive文件一致，headless只打开data页面；未做可见浏览器/真实业务验收 |

B2 本地自包含产物为 `launcher/dist/launcher-p0/AiGoofish.Launcher.App.exe`，225 文件、213,599,406 B；`--verify-package` 实际加载 .NET 10.0.12 x64。1120×760 与 920×640 离屏图已由主代理查看；不等于原生窗口、多档 DPI、辅助技术或干净系统验收。它仍是模拟原型，不是完整便携业务包。

PG runtime 为 `.tmp/dependencies/portable-pg/postgresql-17.11-3-windows-x64`，1,565 文件、141,101,130 B；ZIP 341,325,378 B，官方页面指向的 EDB HTTPS 来源、本地 SHA-256 锁，不伪称发布者签名。真实冒烟在普通用户权限的非 Codex 受限沙箱中运行（EDB 工具需要生成 Windows 降权 token），不需要 UAC 管理员；未注册服务、未连接既有数据库。测试所用的 schema 表仅为隔离 fixture，不是业务 schema 已初始化。

主代理新增 storage package 配置延迟导入，避免显式 schema 入口仅导入 models 就读取业务 `.env`。`test_storage_import_isolation` 禁止导入 config/dotenv 并核验临时 cwd 无写入；与配置/路径/维护合计 38/38 通过。新 Python runtime `python-3.13.15-e67c6b779c81-windows-x64` 绑定 ZIP 与 requirements 锁，265,542,788 B；主代理 `prepare-python.ps1 -Offline` 原生文件/包集合/RECORD/marker 复核通过，快速测试 10/10。旧无 marker runtime 仅保留缓存，不作为发行输入；保留至 2026-09-19，收尾时核验停用后再处理。

## 后续放行顺序

2026-09-20 第二轮继续落地：Sol xhigh `host_reconnect_0920` 正在将恢复接入Core/Host/实际VM与真实退出子进程验收（未放行）；主并行实现backup_postgres/backup_business。真实 `portable_schema_pg_smoke` 两轮PASS，第二轮结果 `PORTABLE_BUSINESS_BACKUP_DB_FILES_KEYS_RESTORE=PASS`：核验PGDATA+instance/schema→同一SQL快照逻辑dump/表行数→业务文件+恢复密钥同包加密→解包→恢复到全新测试库逐表对账，恢复master能解密fixture密文。成功测试集群已停止清理，无真实账号/业务调用。24项备份单测PASS，含停写回调失败/未知范围/缺失密钥/目标在data内拒绝。详细集成边界在docs/PORTABLE_BACKUP_CONTRACT.md，尚无备份UI、目标集群重建与DPAPI重保护，不能标完整恢复。

2026-09-20 本轮主代理独立验收完成：`build-launcher-prototype.ps1 -PythonIntegration` 全链PASS，0warning/0error，Core12、HTTP3、Python生命周期14、Windows19、真实PG/Python Maintenance/Normal重连2场景、离屏UI及自包含EXE通过。发布目录227文件/213,909,105B（不是重新打完整ZIP）。DPAPI控制载荷v2绑定ProgramRoot；旧v1已Stopped能真正重新启动/Ready/停止；live旧v1拒绝；未Ready重连不误报Running且同身份可重试、无控制上下文不可Stop；Normal恢复独立setup token。测试仅模拟旧component释放，不等同实际Launcher crash/Host/UI重开接管。主review-swarm定向安全/状态审查发现项已修复复测。

Python业务侧总定向回归114项PASS，新增backup_inventory6项包含未知范围、无PGDATA遍历、读取失败、限额、只读与reparse拒绝。成功新fixture随套件清理；旧失败目录未动。既有r2完整包仍不包含后续worker/重连/backup修订，不改其已记录hash，不当最终交付。下一有界批次应接Host/UI安全重连并做真实窗口重开验收；完整备份仍须PG逻辑dump+文件+业务密钥恢复闭环，余项见总验收表。

2026-09-20 用户明确“继续落地”：仅派Sol xhigh `reconnect_finish_0920` 收尾Python component重连与测试，主代理审验，不重新完整打包。F盘124,875,411,456B可用。新备份业务文件inventory只读枚举state/assets/results与config/app.env，拒绝reparse/未知范围/访问失败/超限，不遍历PGDATA/cache/logs/本机密钥和运行状态；6项专属测试PASS，相关Python50项回归PASS。该模块不是停写证明或完整备份，PG dump/角色重建/受保护业务密钥导出作为必需独立payload明列。

重连测试根因实证：仿制后端的HttpListener.Start抛Windows错误6“句柄无效”，并非单纯超时；执行代理仅替换测试夹具为TcpListener，不改变真实HTTP客户端，后续必须真实PG/Python重连复核。DPAPI ProgramRoot绑定、旧v1 stopped兼容、重连非Ready状态与重试三项修复仍在本轮实现/验证，不提前标PASS。

2026-09-15 06:31Z续办：F盘已恢复76,639,817,728B（约71.38GiB/7.48%），低盘暂停条件解除，但本轮不重新完整打包。上次仿制Python子进程测试仅看到首项“就绪前退出”，原exec session已不存在，无完整结论；不能认定11项通过。确认无活跃代理后派Sol xhigh `reconnect_verify` 接续测试夹具、component和一次真实栈回归。主审提出3项待修：DPAPI缺ProgramRoot绑定；旧v1已Stopped经GetState被判Unknown导致无法正常升级启动；重连HTTP未Ready后manager已attach却误报Running且不可重试。均未放行，要求针对性复现和测试，不扩Host/UI。历史失败测试目录`.tmp/tests/python-reconnect/939c8bfe9fd34c0db2db153a5516a48f`未删除，准确大小/占用待核验，保留至2026-09-22。goal查询当前为null，不擅自把旧blocked记录改成完成；自动续办范围仍以用户授权和总验收表为准。

2026-09-14 12:27Z续办：无活跃代理；crash_reconnect已留下component+DPAPI store和测试草稿，未交最终实测报告。主代理仅无restore定向编译PASS（0warning/error），未以此标重连验收。主审修正spawn异常时无条件删除pending保护的问题：Process.Start可能已成功而身份读取抛错，现保留pending避免未知子进程下再次启动。后续仍须审查重连未Ready时状态/重试、真实子进程与Host/UI接线。

磁盘外部状态变化：F盘可用26,571,423,744B（约24.75GiB / 2.59%），低于5%安全线；不归因于本任务且不扫描/删除未知目录。暂停新下载、完整打包及新增大型PG fixture，轻量源码/静态测试可继续。r2包与失败诊断保持原样，不自动清理。需要用户释放F盘空间或指定容量足够的新工作位置，才能恢复重型最终验收；不因本任务容量授权而越过低盘保护。

本轮主代理最终Python定向总回归108项PASS，包含容器和worker；UTF8中文header/manifest修订后备份+worker17项再PASS。没有新增下载/完整包，也未删旧产物。安全验收文档单列r2限制；当前唯一实施代理crash_reconnect仍在准备component补丁，尚无可放行重连证据，不重复派工。

2026-09-14 01:08Z续办：确认无在跑代理后仅派Sol xhigh实现Python控制凭据DPAPI持久化/身份重连，限定Windows component及专属测试；主代理并行处理backup/worker，不重复全包。主代理补备份目标盘预估与逐块低盘保护、流式源增长限额，12项容器回归PASS；这些是新实现，不外推为已完成PG一致性备份。发现worker命令缺-I/-B及bootstrap缺dont_write_bytecode，会在业务首次导入后产生额外__pycache__，破坏下次全量manifest预检；已补命令/环境/入口保护，真实无害fixture导入无程序目录写入，worker+路径+bundle16项PASS。r2未包含后续修复，只供空实例启动闭环，限制与安全验收步骤见docs/PORTABLE_LOCAL_ACCEPTANCE.md。

2026-09-14 实际组合包闭环完成：`launcher/dist/portable-integration-20260914-r2.zip` 411,516,831B（约392.5MiB），SHA256 `a2ecb3665e560a4f9d4a8d14b25baf2786e0f9e7ababda9c0ba11d558976e57b`。源payload1,071,176,005B/7,974文件，另含current/manifest；ZIP CRC通过。主代理 `Python.Tests --bundle-acceptance <r2>` 全量验证实际包后，复用只读组件并在专属中文/空格路径执行两个全新host周期：PG初始化/provision/Web/setup页面/正常停机→重开PG/Web/setup/正常停机，PASS。未创建业务账号或触发真实抓取/AI/通知；成功fixture已清理，r2/data不存在。此证据是实际组件与host闭环，不是原生窗口点击/无开发环境/全部P1。

首包问题实证：沙箱生成目录继承CodexSandboxOffline私有ACL，真实用户无法读取current；未绕过或修改权限，改用授权的真实Windows用户重建r2，owner QQQ/PC且可读。另修manifest对.NET自带createdump.exe的误拒，保持精确根文件名+hash覆盖，不放开任意exe。旧失败包 `launcher/dist/portable-integration-20260914/` 与同名ZIP仅诊断保留到2026-09-21，不供验收；ZIP411,516,806B，目录约1.073GB，未清理历史目录。F余52,553,592,832B（5.13%），距5%线仅约1.25GiB，下一轮不得盲目再次完整打包，仍可推进轻量代码/测试；先复用r2程序组件。

下一步：P1仍缺完整PG+文件+密钥备份恢复、Launcher业务认证/配置revision/状态、托盘/关闭选择、crash重连凭据、原生窗口/干净系统和Docker回归；P2/P3按总验收表继续，不能因本次启动闭环成功提前标总目标完成。当前无代理在运行；每小时续办继续挑选不需要新增重型副本的独立任务。

2026-09-14 主代理接续证据：`build-launcher-prototype.ps1 -PythonIntegration` 已独立PASS（Core12、HTTP3、Windows19含manifest/先锁后写、真实PG/Python维护与Normal两模式正常退出、离屏UI、自包含EXE），0warning/0error。B7默认入口是current/manifest验证后手动启动真实host，不再静默模拟；托盘/关闭选择/业务配置等仍未实现。修正UI文案不承诺“正常启动永不运行既有调度”，补COM/LPT上标保留名后基础build再次PASS，发布227文件/213,882,653B。实际完整组合打包正在执行，只有实际包host两周期验证后才放行本切片。

备份容器主代理9项独立unittest PASS：口令往返、篡改/错误密码/不覆盖、超大header预拒、reparse失败关闭、源变更、重复字节可恢复、创建与恢复限额一致、清理失败不覆盖原异常。私有Windows DACL保护明文ZIP与恢复staging；未接PG dump、密钥导出或UI，不声称完整备份。旧失败fixture只读盘点1,427文件/51,652,795B，继续保留到2026-09-20，未删除被拒绝清理的目录。

续办主代理复核（2026-09-13 后续）：旧代理均已不在运行，按用户授权仅重新派发两个不重叠工作面：Sol xhigh 完成 B7，Terra high 补备份容器。主代理持有组合构建器、总测试及验收记录。新增 bundle 验收入口源码将验证实际包字节并在专属中文/空格路径复用只读组件、执行两次真实 host 启停；尚未运行，不能提前标 PASS。

主代理当前证据：94 项便携定向单元/契约测试通过（不包含仍在修改的备份容器）。Python verifier 新增拒绝 RECORD 外可导入文件、非 RECORD 的空哈希及 root 额外文件，锁定缓存用可信构建解释器 `-I -S` 校验 PASS；未下载/重装。组合构建器为每个运行组件文件冻结 SHA，修 Windows 保留名/控制字符/路径别名，许可缺口说明加入 manifest，Windows 提交不覆盖并发目标。dry-run：1,070,429,628 B / 7,963 文件，尚未生成 ZIP；新增测试源码不作为业务 app 打包。

2026-09-13 22:18续办核验：B7与backup代理再次429终止。backup_archive.py已有未验收草稿，但test_portable_backup_archive.py尚不存在，主代理定向测试明确失败（ModuleNotFoundError），不能报备份容器完成。第三方inventory素材已生成，仍须逐项核验与补齐许可文本，不能将nuspec license表达式当完整版权通知。未重复创建代理或重型构建；下一步从B7既有代码和backup缺失测试接续。

完整需求审计见 [总验收状态](../docs/PORTABLE_ACCEPTANCE_STATUS.md)，按P0/P1/P2/P3逐类列出证据与缺口，未将原目标缩减到已有绿色测试。2026-09-13默认种子主审7/7通过；真实PG集成fixture新增固定HEAD资源manifest、系统4组/24权限、Prompt/Bayes和first-admin副本对账，完整SQL/Web回归PASS。种子测试系统TEMP使用已修正到.tmp/tests/portable-seeds，不留测试目录。

B7当前主审反馈待验：host创建与关闭/双击并发、lease前写cache路径、Failed但已停止时锁释放、ready后自动打开Web、manifest额外文件和数据路径范围。便携UI阶段尚无实际bundle启动证据，不对用户报图形交付成功。

2026-09-13组合包主审：修正漏bootstrap/License、默认资源落defaults、app ID含源码内容hash、源文件只查白名单避免遍历整仓、拒绝未知static/templates文件/Windows路径与大小写冲突、复制后hash验证及输出ZIP的CRC验证。4项bundle测试PASS；dry-run约1,070,394,375B/7,961files，按双份峰值+32MiB仍超过低盘安全线，尚未实际复制bundle。UI已交B7接线，不能将旧模拟Launcher成品当交付。HTTP客户端补全响应体阶段deadline，尚待B7总流水线复跑。

2026-09-13 B6关键证据：新增`launcher/tests/AiGoofish.Launcher.Python.Tests/RealPortableStack.cs`，仅复制程序Python源码到唯一测试app目录，复用锁定runtime，不复制真实配置/业务文件。`build-launcher-prototype.ps1 -PythonIntegration`显式真实验证通过并清理成功实例。首轮切换模式被旧Stopped记录的mode/port严格匹配误拒绝，已将新启动前确认旧身份退出与当前运行身份严格匹配分开；旧失败实例PG按PID/starttime/exe/PGDATA核验后smart停止。原Python.Tests假子进程套件仍未执行，不计入通过测试数。

保留失败诊断：`.tmp/tests/portable-real-stack/a9c76ee5021f40b9bd38e41ebbc125ee` 为本次新建可重建fixture，PG已smart停止、维护Python已退出；递归清理请求被审批拒绝，未绕过。保留至2026-09-20，准确大小待只读盘点，用户确认前不删除；不是可用实例/业务数据。

2026-09-13 后续主代理接续：B6/Web排空/构建器三个代理再次429终止，未重复重试。主代理修正排空Cancelling状态串行、恢复失败明确状态、scheduled job入场门控、遗漏的状态收尾task注册，新增4项排空竞态测试PASS，相关17项回归PASS。真实Web HTTP子进程鉴权shutdown后exit0验证通过；首次设置三资源+安全响应头在PG Web fixture验证通过。B6已修独立runtime路径、TEMP/TMP、敏感record.ToString与normal shutdown协议适配；新测试项目已还原并编译0warning/error，Core12/Windows15/UI发布回归PASS，但Python测试夹具仍为未接线假子进程草稿，尚未执行或放行B6真实栈。不得把build成功当该测试套件已通过。

2026-09-13 续办核验：B5 与 worker 第二批代理均因 429 终止，未假称后台仍在运行。主代理复跑 worker/app_paths/auth 12/12 通过并完成当前 diff 初审；发现 browser 被限制在 app 组件内与独立 browsers 布局冲突、worker 临时根及控制凭据继承需修，已将精确修复任务交回原代理接续。B5 目前仅部分基础文件，WindowsPostgresComponent 与真实集成未完成；已在原代理恢复有界实现，不重复下载或创建副本。F 盘当前约 52.3 GiB 可用，低磁盘保护仍有效。

最新主代理回归：62 项 portable 定向测试通过；真实 schema PG 冒烟 PASS；内置 Python 维护 HTTP 为 401/401/503/200，随后鉴权停止 202、真实 exit 0，集群停机清理完成。`src/web/main.py` 新增便携 static/templates/images/avatars 绝对挂载与日志路径接线，尚未运行完整业务入口，不能据此标记业务 Web 可用。

2026-09-13 新证据：schema 的首管理员真实测试改用普通 app role，并仅授予版本表 SELECT，验证通过。真实 PostgresAdapter 会话子进程验证登出只撤销当前会话、改密事务撤销、角色实时读取、停用撤销通过。两个早期失败测试目录 ihgun7r2/s1rylg4s 已逐个核验 pg_ctl status=3 与无 reparse 后清理，释放 100,624,679 B；原因是测试误用不返回 password_hash 的接口，未改业务查询契约。Docker CLI 存在，但 dockerDesktopLinuxEngine 管道不存在（服务未运行）；未启动 Docker Desktop 或拉镜像，容器构建/启动回归仍待满足运行条件，不拿源码分支单测冒充容器通过。

Chromium保留缓存至2026-09-20：`.tmp/dependencies/portable-browser/downloads/chromium-1200-win64.zip` 178,067,817 B；`chromium-1.57.0-r1200-4b4d412c65ff-win64` 404,442,703 B（309文件含manifest），用于后续组合打包。SHA256为4b4d412c65ffa6486eebdeb7ca05186c9598f90a60fb67e201fb8afa27776ab6，仅官方CDN HTTPS获取后本地锁定；未验证发行者签名。主代理复跑后F余55,503,323,136 B（5.42%），距离5%安全线约4GiB，后续打包仍须核算峰值。

首次设置页采用计划蓝白系统字体和ui-ux-pro-max的字段标签/错误摘要/键盘聚焦规则，未采用偏营销页检索模板。`tests/portable_setup_ui_smoke.py` 显式本地拦截路由，不访问业务/外部API，验证空表错误不POST、错误token提示后清秘密、201成功和登录链接、浏览器storage为空。截图保留在launcher/dist/launcher-p0-verification/setup-1120.png及setup-375.png作为交付证据，无真实凭据；无临时browser profile残留。

1. 主代理审查并复跑当前两个批次，不把离屏渲染当成原生窗口/DPI 全覆盖。
2. 内置 PG/Python 组件获取、版本锁、校验和非管理员隔离启停；真实 SQL 与维护入口身份链路。
3. 数据路径接线、独立 schema 初始化、首次账号与凭据保护；普通业务 Web 便携模式与 Docker 回归。
4. 完整本地 ZIP、组件版本清单、备份恢复和故障注入；P1 逐项验收。
5. 离线升级/回退与在线组件更新按规划推进；发布源、永久签名身份等缺失决定明确记录，不伪造已发布能力。

每次续办先读本记录与规划的当前状态；只从未完成项继续。里程碑完成必须附真实命令、结果和未覆盖项。
- 恢复安全切片：新增固定 `portable_restore.py` stdio 入口（严格字段/维护证明/错误不回显）、bundle/bootstrap 白名单；新增 `WindowsRestoredSecretsImporter`，只在全新已标记 PG17 目标且目标 DPAPI 文件不存在时导入 handoff，保留 URL-safe app/probe 口令原文，DPAPI 写后 Load 往返校验，成功/失败擦除并删除 handoff，清理失败拒绝成功。Windows22/22、Python restore/archive8/8、bundle/python13/13，便携全定向149/149；.NET10 Release0/0。未接目标 PG 初始化、Host 进程编排、Web/worker 或自动切换，不能称完整恢复。
