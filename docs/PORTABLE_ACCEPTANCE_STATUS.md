# 便携版验收状态

## 当前 beta 基线（2026-09-27，r10）

按项目所有者决定，以 r10 已通过的本机启动验收形成 `1.1.0.0-beta` 源码提交。普通用户中文路径解压后的首次启动、同窗口停止重启、运行中安全退出、重开启动及诊断界面已通过；尚不代表干净 Windows、完整业务流程、DPI、最新 Docker 回归或备份恢复稳定性全部通过。旧 r10 包仍内含 `V1.0.4.5`，本次版本提交不修改旧 ZIP。准确范围见 [beta 版本说明](RELEASE_1.1.0.0-beta.md)。

## 历史阶段结论（不代表当前 beta 决定）

2026-09-27 最新结论：**r7 仍暂不公开放行**。r6 的同 Host 重启问题已修复；自动/固定 Web 端口已实现，r7 包内程序集的重启、真实端口切换/回退、生产 ViewModel 自动避让、配置 CAS 均 PASS。备份恢复最后串行运行 PASS，但此前两次独立串行失败根因未定，不能用单次成功覆盖；原生桌面、干净 Windows、跨 SID 门槛亦未完成。包身份、证据、失败留存见 [本轮报告](PORTABLE_PORT_FIXES_20260927.md)。以下日期段和表格保留各自历史范围，不代表最新总放行。

2026-09-26 发布前补验结论：**r6 暂不放行**。包内实际程序集已复现首次初始化后同一 Host 的端口应用与自动回退失败，诊断指向 provision 辅助进程身份生命周期；详见 [补验报告](PORTABLE_RELEASE_ACCEPTANCE_20260926.md)。两周期重建 Host 启停、包扫描、ZIP 校验及 Headless 专项通过，不覆盖此缺陷。下方较早 PASS 仅对各自测试范围有效。

2026-09-26 源码审查修复及 r6 新构建证据见 [本轮修复记录](PORTABLE_REVIEW_FIXES_20260926.md)。r6 实际包扫描、Host→PG 配置 CAS 与备份恢复 E2E 已 PASS；Docker 本轮因引擎未运行未验，原生/干净 Windows/跨 SID 门槛不变。下表 r5 是历史特定包证据，不能外推到其他源码或包。

更新：2026-09-24。此表按 PORTABLE_LAUNCHER_PLAN.md 的完整范围审计，不缩减为当前已有实现。未勾选的阶段不得标记完成；局部 PASS 不是产品交付。具体命令和缓存期限见 launcher/WORK_LOG.md。r1–r4 为历史证据；r5 已完成真实 Host→PG CAS 与备份/恢复、发行包扫描及 ZIP 校验，但仍是 `preview-integration`，不是公开发布放行依据。

| 要求 / 原规划来源 | 当前权威证据 | 状态 / 仍需证明 |
| --- | --- | --- |
| D01/D18/D19 Windows x64 Avalonia、自包含、不依赖 Docker | r5 包通过真实 Host→PG CAS 与备份/恢复；Launcher Core/HTTP/Python/Windows/真实栈/UI Smoke 构建与回归通过 | 部分完成；r5 仍为 `preview-integration`、未公开放行；最低 Windows / 无工具干净系统及原生窗口、Explorer 托盘、多档 DPI 未验 |
| D07 锁定 Python/依赖/Chromium/PG，正常启动不安装 | r5 预览包 `launcher/dist/portable-acceptance-frozen-20260924-p1-r5/`，8,079 文件 / 1,089,294,204 B；ZIP 413,849,907 B，SHA-256 `9f01503a2570f4b121143646979461ec83fcd5a6c1e7871567434e6ee605b91d`；8,077 payload entries 扫描无 findings，ZIP member/CRC/size/hash 均通过；8,077 包输入及 111 控制输入/HEAD 前后指纹一致 | 包层构建与扫描通过；r5 尚未完成完整产品放行，干净 Windows 离线验收与原生体验未完成 |
| D08/§4 程序、数据、缓存分离 | RuntimePaths、便携配置与消费者/worker定向测试；worker和bootstrap已禁止生成程序目录bytecode，真实无害fixture验证 | 部分完成；r2未含后续worker修复，仅空实例启停可验；完整业务写路径/移动程序/只读失败待验 |
| §6 单实例、归属、端口冲突、受控停止 | Windows TCP control Bearer 归属 guard 的普通本机用户 fake 测试与只读审查通过；托盘生命周期 Headless Smoke 通过（后台隐藏/恢复/安全退出及降级路径） | 最终 Host E2E 尚未复验 TCP guard；Explorer 原生托盘、全停后的真实 PG/UI 重启、双击唤起、真实崩溃/断电场景未验 |
| §6 首次初始化中断不自动重建 | PG进行中marker、非空无marker拒绝；provision重复拒绝；初始化中断只读分类/安全恢复指引已实现并通过独立审查及 Core/Windows fake/Headless 测试 | 拒绝保护与恢复指引代码通过；真实断电/操作恢复演练未验，不计完整故障注入 PASS |
| §7 schema、角色、无默认管理员 | 真实PG schema/provision、app无DDL、probe不可读用户；首次管理员空表并发和回滚；固定HEAD默认资源及首次用户副本实际对账 | 基础通过；版本迁移属于P2未完成 |
| §7 凭据保护、跨机器受保护恢复 | DPAPI当前用户、实例绑定、篡改/移动测试通过；业务备份将两个业务密钥写入受口令保护的 `recovery-keys.json`，恢复时生成新的角色口令并在当前 Windows 用户下重新 DPAPI 保护；r3 同一 Windows 用户/SID 的真实 Host→PG E2E 通过 | 本机及同 SID 恢复路径有证据；使用另一 Windows 用户或另一台机器的恢复演练仍缺证据，不能据此宣称已验跨用户/跨机器 |
| §3.9 首次引导、业务认证和控制分权 | Web真实PG setup/login/logout/reopen；独立setup token；可撤销会话 | Web基础通过；Launcher登录/配对/用户切换缓存及UI展示未完成 |
| §3.6/§6 安全排空与退出语义 | ASGI 请求/后台任务/定时入口排空及真实 HTTP exit0 有旧证据；关闭时后台运行/安全退出/取消、托盘生命周期已有 Headless Smoke | 原生窗口关闭/恢复、Explorer 托盘操作、真实在途任务退出及最终 Host 集成未验 |
| §3 首页/控制台/导航/设置/目录 | 配对/当前用户/精简 AI 配置有 App 生产接线与已有 Headless Smoke；本地偏好 Headless 与只读 review 通过；恢复状态机/UI 已实现并通过 Headless Smoke；诊断按钮、预览和保存已在 MainWindow/ViewModel 生产接线，`WebPortUiAcceptance.RunDiagnosticPreviewAcceptanceAsync` 覆盖真实 handler 并逐字节核对预览与保存内容 | 新增 App fake seam 矩阵受测试进程文件锁阻断，未验收；原生窗口操作仍未验；恢复只验 Headless，原生 UI 未验 |
| §3.4 1366×768/DPI/键盘/长日志 | 模拟窗口1120/920渲染、日志有界Core测试，setup页面1120/375浏览器回归 | 部分；原生Windows 100/125/150/200%与辅助技术/压力未验 |
| §3.8 配置唯一来源/revision/实际生效 | 便携 app.env 白名单/来源隔离通过；AI/代理读写共用 PostgreSQL 事务内 `config_revision` + `config_id` CAS；r5 实际包真实 Host→PG CAS 在 46123/55432 下精确返回 `CONFIG_PG_CAS_ACCEPTANCE=PASS`；ZIP 前后指纹一致 | CAS E2E 已通过；Web 端口 Platform 安全 store/Host/gate 定向通过且 App 已接线，但 fake seam 矩阵受测试进程文件锁阻断，真实端口切换/失败恢复未验 |
| §3.10 只读状态/新鲜度/用户隔离 | Launcher 每30秒采样受管组件，运行组件离开 Running 后摘要转故障且不自动重启/停止；展示采样时间与30秒新鲜阈值、Launcher进程内存与Launcher所在盘容量；Core 观察异常测试通过 | 真实PG/Web故障注入及原生 UI 未验；无 Web 业务/用户摘要；BackingUp/Stopping期间采样受操作门保护并保留最后观测时间，可能显示过期 |
| §3.11 AI检查/保存/主动测试分离 | AI/代理配置 CAS、去重与 TTL 定向测试/审查通过；保存与手动 AI 测试仍分离 | 未使用真实账号或付费 AI；最终 Host E2E 与真实网络策略未验 |
| §7.3 P1完整数据库+文件+密钥备份恢复 | AEAD 容器、业务文件 inventory、PG17 双集群逐表/文件/密钥对账有历史证据；r5 实际包真实 Host→PG 备份/恢复 E2E 精确输出 `BUSINESS_BACKUP_RESTORE_HOST_E2E=PASS`；旧 retained fixture 2,756 文件 / 100,725,670 B 前后指纹 `cf2a7fba072af94ee24bc132b4fda2233878a9c22d51c82857eb1e20c30571c8` 未变 | r5 备份/恢复目标 E2E 通过；恢复后业务 Web/worker 完整衔接、初始化中断恢复定向验证与原生流程仍需补验 |
| §9 诊断脱敏/日志卫生/低磁盘 | Launcher 导出仅白名单版本/平台/总体状态/固定组件ID与状态/采样时刻，最多16 KiB；导出前展示准确JSON预览、用户选目录、同目录临时文件后原子移入且禁止覆盖；失败 UI 仅显示通用消息；专属测试通过敏感值过滤、大小边界、目标冲突及临时清理；Headless UI 验收逐字节确认保存内容等于不可变预览 ticket 内容 | 诊断目前不含日志或配置；生产 UI handler 的 Headless 流程已验，原生窗口操作仍未验；日志保留策略/峰值恢复未完成 |
| §10 P1 ZIP/许可清单/交付说明 | r5 预览目录/ZIP 已构建、扫描并校验；8,077 payload entries 无 findings，ZIP 成员/CRC/size/hash 通过，包与控制指纹一致且符合预期 | r5 仍非公开发行 PASS；干净 Windows、原生 UI 与其余 P1 门槛未完成 |
| §1.3 Docker共享后端回归 | Engine 29.7.2；Docker 有效 build context 前后均为 315 files / 74,195,395 B，SHA-256 `8436ce52d43b6ebfb5e0f95172e0ea31db755ad6f7733ec4521163ede0300bde`；独立合成 app+PostgreSQL normal lifespan、PG healthy、schema ensure、health 200、login 200、bad login 401 均通过；原 keygate app/PG 容器 ID 健康状态及四卷保持不变 | Docker 冻结源码回归 PASS；本轮容器、空卷、网络、镜像与 3 文件 / 75,594 B 合成 fixture 已精确清理，BuildKit cache 未 prune。此项不代表 Windows r5 公开发布放行；原生 GUI、干净 Windows 与跨 SID 恢复仍未验 |
| §8 P2签名离线更新/独立更新器/事务恢复 | 尚无更新器交付 | 未实现；坏签名/路径穿越/炸弹/断电/不兼容schema回退/PG版本保护需验 |
| §8 P3在线更新/可信来源/断点组件复用 | 未配置发行源或维护签名身份 | 开发机制未完成；上线运维选择待用户，未授权发布/购买/签名私钥操作 |
| §3.12 用量增强（P2之后） | 未开启采集、不显示假零值 | 未实现；保留期限需确认后实施，费用/余额等后置 |
| §10 P4旧JSON迁移/安装版/外置数据向导 | 明确后置 | 不纳入首个便携交付，不自动迁移或删除旧数据 |
| §12 MIT与真实业务边界 | License未更换；测试使用隔离fixture无真实抓取/AI/通知 | 持续守护；未发版本、未购买证书、未操作真实账号 |

## 下一次产品放行门槛

1. B7真实窗口+完整目录包通过首次/再次启动、引导、正常停止，且布局hash校验与用户数据保护成立。
2. 完成P1剩余认证配置、诊断、备份恢复、关闭/托盘/超时行为和最终ZIP安全扫描；补原生与Docker验证。
3. P2/P3保持原规划范围；可独立实现的本地机制继续推进，只有真实发布源/签名运维决定才列外部依赖，不能因此将未实现的机制假称完成。

P1 状态补充：r5 实际包的真实 PG CAS 与 Host→PG 业务备份/恢复 E2E 均已通过；ZIP SHA-256 为 `9f01503a2570f4b121143646979461ec83fcd5a6c1e7871567434e6ee605b91d`。r5 为 `preview-integration`，不是公开发布 PASS。Docker 冻结源码回归通过：有效上下文前后均为 315 files / 74,195,395 B、SHA-256 `8436ce52d43b6ebfb5e0f95172e0ea31db755ad6f7733ec4521163ede0300bde`；干净 Windows x64、跨 SID 与原生 UI 仍未验收。初始化中断恢复已有只读分类/恢复指引实现、独立审查及 Core/Windows fake/Headless 测试，但没有真实断电/操作恢复演练，不能标完整故障注入 PASS。P1 初始 schema 基线已有隔离 PG 证据；版本化 schema migration 仍归 P2，不改变该分层。
