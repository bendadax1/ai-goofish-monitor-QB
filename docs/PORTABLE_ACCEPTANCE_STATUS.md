# 便携版验收状态

## 第一批加法：本机健康、用户 AI 高级参数、日志控制（2026-09-28，源码与增量构建）

- 首页新增本机健康卡：复用组件观测、管理地址/端口和观测新鲜度，详细信息仍进入诊断；不将账号 AI 状态混入本机健康。保留品牌资源及右下角固定主操作，1120×760 / 920×640 离屏检查通过。
- AI 配置明确当前授权账号归属，新增切换账号及退出前未保存草稿确认。高级区只接入已走用户 `extra_config` / 任务运行环境的 tokens 字段名及上限，沿用配置 ID/revision 条件更新和密钥不回显规则。字段名留空保留“不注入输出长度参数”的既有语义，不自动保存显示默认值；高级草稿须先保存才能手测。切换/失效清空私有数据，版本冲突重新读取权威配置；保存不触发 AI 请求。thinking、JSON 格式、图像输入仍有全局调用链，本轮不迁移、不伪装成个人设置。
- 日志新增全部/警告与错误/仅错误筛选、暂停更新及当前视图导出。暂停只冻结窗口快照，后台记录继续；两个视图统一使用既有类型事件的严重性。导出先展示只读预览，再选择目录，写入确认时的快照；UTF-8 无 BOM、2 MiB 上限、新文件不覆盖，先暂存再移动，失败尽力清理本次暂存。取消不写文件，不上传、不清理历史日志，不接入业务任务日志。
- 最后两轮完整增量构建及所选验收通过，0 警告/0 错误：Core、HTTP、Python 生命周期、Windows 进程、UI Smoke、启动诊断、Web 端口 UI。Python 配对、AI 配置版本、手测及健康缓存合计 50 项通过。新增高级参数协议/缺省与空值/输入校验/CAS/退出清空、日志筛选/暂停/恢复/不可变导出/真实弹窗取消与保存/写入失败测试。早期轮次修复了原始诊断严重性与摘要不一致，以及离屏测试未泵送异步写入；另一次生命周期测试失败，后两轮相同所选套件通过，未为此修改生命周期实现。
- 最终截图 `.tmp/tests/portable-acceptance/20260928-151521-129ef9387ec5/`，32 文件 / 2,024,573 B；成功比较轮 `20260928-151249-956671f648b0/`，32 文件 / 2,025,048 B。截图为真实 Avalonia/Skia 离屏控件和合成状态，不代表原生 Win32/DPI 或发行包验收。源码、本地增量构建已升级，旧 r16 ZIP 未覆盖；未改 Docker 路线、抓取频率、通知、数据库迁移或用户 AI 调用行为。
- 复用共享增量构建 746,075,339 B / 341 文件，没有新增依赖。登记 `.tmp/tests/launcher-upgrade-20260928/README.md`，视觉证据及合成导出留存至 2026-10-05 复核；未删除历史候选、缓存或业务数据。收尾 `.tmp` 可读下限 25,694,701,318 B / 153,890 文件，10 处历史枚举失败，F 盘剩余 100,380,745,728 B。Git 空白检查及本轮源码 UTF-8 无 BOM 检查通过。

## 日志双视图与共享操作（2026-09-28，源码与增量构建）

- 继续借鉴 QTB 的界面组织：日志页顶部集中状态、诊断预览及主操作，停止、安全取消、取消恢复共用原 handler 和启用/显示状态，日志页隐藏重复底栏；首页右下角主按钮位置不变。未复制参考项目代码、资产或引入依赖。
- 新增「活动摘要 / 原始日志」切换并在页面导航间保留本窗口选择。摘要将既有结构化启动诊断翻译为短中文，补充协调器告警/错误，模拟模式保留模拟生命周期信息；新收到事件在上方、独立保留最近 100 条。原始日志仍为既有 300 条 UI 记录，顺序和内容不变，完整文件入口仍为「打开日志目录」。两视图有空态，切换不清空记录；不增加磁盘读写、网络采集、AI 调用或业务任务事件。
- AI 配置页与本轮改动前逐字一致；未修改服务编排、账号、数据库、抓取和通知行为。首轮新增空态测试受进程级历史诊断回放影响失败；修正为仅清空隔离展示 ViewModel 的集合后，两轮完整增量构建和所选验收通过，0 警告/0 错误。Core、HTTP、Python 生命周期、Windows 进程、UI Smoke、启动诊断、端口 UI 均通过；新增摘要翻译/严重性、诊断去重、100/300 条容量、切换不丢记录、顶部操作边界及从日志页停止模拟服务的回归。UTF-8 无 BOM、Git 空白检查通过。
- 最终截图 `.tmp/tests/portable-acceptance/20260928-142733-1d8760043e12/`：26 文件 / 1,755,975 B；成功比较轮 `20260928-142543-1fc0bf2d4641/`：25 文件 / 1,700,151 B；首次失败轮 `20260928-142205-896142b87bff/`：15 文件 / 1,067,724 B。截图使用真实 Avalonia/Skia 离屏控件及合成事件，故障文本是测试夹具，不是本机真实故障；不代表原生 Win32/DPI 或发行包验收。旧 r16 ZIP 未覆盖。
- 负责人本聊天，三轮视觉证据和登记 `.tmp/tests/launcher-activity-20260928/README.md` 保留至 2026-10-05 复核。增量构建共享目录复用 745,942,693 B / 341 文件；没有安装依赖或删除历史产物、缓存及业务数据。最终 `.tmp` 全量可读盘点与历史不可读目录数量记入 README。

## 首页右下角主操作（2026-09-28，源码与增量构建）

- 按用户截图将首页主操作移至窗口右下角，232×64、20px 居中文字，沿用紫色主题；固定在滚动区之外，首页底部无分割线，停止、取消和取消恢复保留在主操作左侧。首页状态卡不再有按钮，其他页面保持原有紧凑底栏，未改变状态流转、首次设置、Web 打开或 AI 配置。
- 只读参考 `F:/project/QTB-v2/launcher/`：`ui/index.html` 的首页底部操作区、`ui/css/launcher.css` 的分区与底部对齐、`ui/js/app.js` 的统一按钮状态值得借鉴；控制台的活动/原始日志双视图仅记录为后续候选，不新增本轮功能。参考为 Python/pywebview，本项目继续 C#/Avalonia，不复制其代码或资产、不引入远程字体或 CDN 依赖。
- 两轮增量构建和所选验收通过，0 警告/0 错误；Core、HTTP、Python 生命周期、Windows 进程、UI Smoke、启动诊断与 Web 端口 UI 均通过。新增 1120×760 / 920×640 右下角距离断言、长状态及滚动时按钮位置不变、各状态下单主操作和必要安全按钮可见性检查。AI 配置区与本轮改动前逐字一致；UTF-8 无 BOM 和 Git 空白检查通过。
- 最终离屏截图 `.tmp/tests/portable-acceptance/20260928-140744-6a2a018ec71c/`，21 文件 / 1,474,705 B；首轮比较截图 `20260928-140555-4e3aebbc535d/`，21 文件 / 1,474,352 B。使用真实 Avalonia/Skia 渲染和展示/内存夹具，不代表真实服务、原生 Win32/DPI 或发行包验收。未重新生成或覆盖 r16 ZIP。
- 负责人本聊天，截图保留供用户比较，2026-10-05 复核，详见 `.tmp/tests/launcher-right-corner-20260928/README.md`。共享增量构建复用 745,881,913 B / 341 文件；没有安装依赖或删除历史产物、参考项目和业务数据，最终 `.tmp` 盘点记在同一 README。

## Launcher 界面减法预览（2026-09-28，源码与增量构建）

- 首页只保留品牌欢迎区、状态与主操作，去掉重复快捷入口、组件明细和 AI 摘要卡；AI 配置页 XAML 与本轮改动前逐字对比一致，账号配对、配置保存和测试行为未改。数据目录入口移到设置，日志目录入口移到日志页，组件状态归入诊断；诊断预览、状态刷新和备份恢复保留。
- 缩短设置及首次设置说明，端口按钮改为「保存设置」「应用并启动」，保留停机、启动和失败回退提示。首页没有可用安全操作时隐藏空底栏，有停止、取消或取消恢复操作时保留；其他页面仍有共享主操作。状态长文改为换行，未改后台状态流转、凭据、数据库、抓取或通知。
- 两轮 `scripts/portable/build-launcher-prototype.ps1 -NoPublish -Incremental` 均通过，0 警告/0 错误；Core、HTTP、Python 生命周期、Windows 进程、UI Smoke、启动诊断和端口 UI 通过。新增首页底栏显示/通知、目录入口归位、AI 控件保留及两种窗口尺寸截图检查；端口完整面板复用内存 Fake Host 验证按钮可滚动到达。
- 最终截图 `.tmp/tests/portable-acceptance/20260928-134918-1786aec3e1d2/`：19 文件 / 1,280,970 B；首轮比较截图 `20260928-134648-b520f2162d8f/`：17 文件 / 1,171,823 B。均为真实 Avalonia/Skia 离屏渲染，状态来自展示夹具或内存 Fake Host，不代表真实服务或原生 Win32/DPI 验收。仅更新源码及本地增量构建，r16 ZIP 未重新生成或覆盖。
- 负责人本聊天，截图及 `.tmp/tests/launcher-slim-20260928/README.md` 保留至 2026-10-05 复核。复用的增量构建目录 `.tmp/build/launcher-refactor/full/` 为 745,879,087 B / 341 文件；未新增依赖，未删除历史产物、共享缓存或用户数据。首轮收尾盘点 `.tmp` 可读下限 25,679,187,472 B / 153,660 文件，10 处历史枚举错误，F 盘可用 100,403,253,248 B；不可读部分未记作零。Git 空白及本轮源码 UTF-8 无 BOM 检查通过。

## Launcher 品牌视觉统一（2026-09-28，源码与增量构建）

- 首页替换夜市主视觉，直接链接现有 `images/login-bg.png` 与高清 Logo 为 Avalonia 内嵌资源；不生成或维护第二套品牌图片，旧夜市源文件保留。暖白背景、暖黄欢迎区、紫色渐变主操作、浅紫导航选中态、柔和卡片边框统一；设置与控制台不铺装饰图片。
- 首页主操作移入服务状态卡，首次设置时显示「设置登录密码」，正常就绪显示「打开管理页」。其他页面保留底部主操作；两者共用原执行入口和启用状态。首次设置提示合并到状态卡，不再提供竞争的第二个设置按钮。未改动启动/停止、授权票据、账号、数据库、抓取、AI 或通知逻辑。
- 最终命令 `scripts/portable/build-launcher-prototype.ps1 -NoPublish -Incremental` 通过，0 警告/0 错误；Core、HTTP、Python 生命周期、Windows 进程、UI Smoke、启动诊断及 Web 端口 UI 回归通过。新增品牌离屏夹具覆盖资源加载、停机/首次设置/就绪/忙碌状态、绑定通知、首页单主操作、其他页面共享禁用状态，以及 1120×760 / 920×640 主操作无需滚动即可见。
- 最终截图为 `.tmp/tests/portable-acceptance/20260928-130948-286763a29577/`（11 文件 / 1,187,783 B）。它们由真实 Avalonia/Skia 控件离屏渲染，品牌状态截图注入展示夹具、未启动真实服务；不是原生 Win32/DPI 验收或发行包验收。源码和本地增量构建已更新，未发布或覆盖 r16 ZIP。
- 负责人本聊天，2026-10-05 复核。两轮比较截图分别留在 `20260928-130201-f3e8640968bc`（528,968 B）与 `20260928-130701-24cce558c327`（1,215,989 B），同属 `.tmp/tests/portable-acceptance/`；本轮只保留这三轮视觉资料。复用的 `.tmp/build/launcher-refactor/full/` 为 745,904,550 B / 341 文件，不是本轮全新增，供后续增量构建继续复用。运行器成功捕获与测试临时夹具已按既有机制收尾，登记文件 `.tmp/tests/launcher-brand-20260928/README.md` 保留。盘点 `.tmp` 可读下限 25,676,859,106 B / 153,631 文件，10 处枚举错误，F 盘可用 100,398,817,280 B（最终微调前采样）。没有删除历史产物、修改 ACL 或安装新依赖；Git 空白检查与改动文件 UTF-8 无 BOM 检查通过。

## r16 后续源码：首次设置不再限时填写

用户确认取消兑换后的 10 分钟填写期限。交接票据仍为 60 秒单次有效；设置会话改为浏览器会话 Cookie，服务端仅在内存保留，设置完成或服务退出后失效。同一浏览器重新打开复用有效授权；最多 8 个授权浏览器，达到上限拒绝新增而非踢掉正在填写的会话。未完成设置可下次继续，不生成默认密码或提前创建用户。本改动未重新打包进 r16。

### 统一账号规则（2026-09-28，源码更新）

首次设置、新增用户、修改密码及管理员重置共用 `src/account_policy.py`：密码至少 8 个字符，允许纯数字，不要求大小写或符号，UTF-8 最多 72 字节（bcrypt 上限）；新用户名 3–50 个字符，不接受首尾空白或控制字符。普通 API 请求模型和 PostgreSQL 明文写入入口复用规则，浏览器的提示及参数由后端模板注入，所有密码表单共用一份 JS 校验器。个人资料页改密字段由 `current_password` 修正为后端要求的 `old_password`。

兼容边界：旧账号登录、密码哈希验证、已存在账号及会话机制不变；Docker 环境变量初始化与已有哈希导入不追溯套用新密码要求。便携数据库会话与旧签名 Cookie 暂不迁移。上述源码尚未重新打包，旧 r16 不含这些更新。

验收：账号规则、前后端 Unicode/长度边界一致性、两个个人改密表单处理器、旧短密码登录与两种会话模式、schema 无配置副作用、Web 门禁及打包输入共 52 项 Python 回归通过；3 个 JS 文件语法检查通过。复用现有 Playwright/Chromium，桌面和 375px 真实页面验证 7 位拒绝、8 位数字建号与自动登录、CSP 及失败回退，`PORTABLE_SETUP_UI=PASS`。隔离真实 PostgreSQL 验证首次设置、新增用户、管理员重置、自助改密均拒绝 7 位、接受 8 位数字，改密撤销旧会话及重新登录/登出正常，`PORTABLE_SETUP_PG_WEB_FLOW=PASS`、`PORTABLE_SETUP_PG=PASS`，成功集群已停机并由现有夹具收尾。未运行 Docker 容器级验收或重新生成发行包。

留存：`.tmp/tests/account-policy-20260928/` 两张截图合计 1,311,289 B（另有 README），负责人本聊天，2026-10-05 复核。首次数据库验收因新加的测试请求遗漏 Origin 被既有保护正确拒绝；已补头后重跑通过，失败夹具 `.tmp/tests/portable-pg/集成 冒烟-schema-a_ar2b0w/` 为 50,406,776 B / 1,376 文件，受管集群已停止、凭据已脱敏，保留状态用于复核，未经确认不删除。收尾盘点 `.tmp` 可读下限 25,672,680,941 B / 153,613 文件，10 处枚举错误，未将不可读目录记为零；F 盘可用 100,404,101,120 B。未清理历史产物、共享缓存或用户数据；未新增依赖。

## 首次设置体验优化（2026-09-28，r16）

- 新候选：`launcher/dist/portable-20260928-beta-setup-r16/`，ZIP 为同名 `.zip`，389,037,035 B，SHA-256 `8140a8ee22e9370f95aee2be91ed24e7f41aec1da52bbd91ce26ea539f1e7bf0`。独立扫描 `PASS`、`findings=[]`，展开后 8,076 文件 / 986,407,890 B；保留 `preview-integration` 状态，不覆盖 r15 或既有数据。
- 首次设置继承原 `login.html` 的背景、品牌卡片和按钮。默认管理员 `admin`，只设置/确认密码；取消可见设置码及剪贴板复制入口。Launcher 用独立 setup credential 申请 60 秒单次票据，URL 片段仅携带该票据并由页面立即清除；兑换成实例隔离、HttpOnly、SameSite=Strict、`/setup` 路径、10 分钟有效的临时 Cookie。后端只在内存保存票据/会话摘要，完成设置后清空，仍保持空用户表事务、Host/Origin 校验、限流和业务门禁。旧受保护 setup API 为兼容验收保留，不向普通页面暴露设置凭据。
- 设置后调用既有 `/login`，复用会话与调度规则；自动登录失败明确提示账号已创建并提供登录入口。已有账号不重建、不改密码；Docker 仍使用原登录流程。未安装新依赖，未变更抓取、AI、通知或用户隔离规则。
- 验证：77 项 Python 定向回归通过；真实 Chromium 桌面/375px 页面、CSP、无外部请求、无授权拒绝、同页重新授权、刷新保持授权、密码不一致、422 可重试、自动登录及失败回退通过。真实 PostgreSQL 专用验证 `PORTABLE_SETUP_PG_WEB_FLOW=PASS`、`PORTABLE_SETUP_PG=PASS`，覆盖真正建号、登录、登出撤销及已有用户重启，并完成自有测试集群停机清理。
- Launcher 完整 Release 编译 0 警告/0 错误，Core、HTTP 契约、Python 生命周期、Windows 进程、UI Smoke、启动诊断、端口 UI 通过；新增票据客户端地址与响应边界用例通过。r16 实包 `BUNDLE_ACCEPTANCE=PASS`，实际 Host 自动申请并兑换票据、重放被拒、PG/Web 启停通过。未覆盖干净 Windows 人工验收，不宣称 Docker 容器重建已验证。
- 发布输入：`launcher/dist/launcher-p1-acceptance-20260928-100135-aff971a17516/`；构建清单和可重建输出 `.tmp/build/portable-acceptance/20260928-100135-aff971a17516/` 为 823,398,446 B，保留供包指纹追溯。配套 `.tmp/tests/portable-acceptance/20260928-100135-aff971a17516/` 729,165 B；页面/Launcher 截图 `.tmp/tests/portable-setup-20260928/` 约 2.05 MB。上述候选和证据由本聊天负责，2026-10-05 复核；共享增量构建缓存继续复用，不删除其他任务输出。
- 三个失败夹具保留：`.tmp/tests/portable-pg/集成 冒烟-schema-s0xe0qma/`（沙箱 restricted token 错误）、`.tmp/tests/portable-pg/集成 冒烟-schema-aydr6rxv/`（整套旧 schema 验收在 Web 阶段前失败）、`.tmp/tests/portable-setup-pg/集成 冒烟-schema-vibfpk9y/`（专用 Web 用例完成，但新父目录被既有清理器拒绝；已修正测试并完整复跑通过）。这些是隔离测试状态，程序结束前执行受控停机；跨账号 ACL 导致本轮最终逐项占用无法完整读取，大小未核实，不强改权限、不自动删除，2026-10-05 复核。最后 `.tmp` 可读下限 24,895,185,723 B / 132,142 文件，20 处目录无法枚举，实际总量更大；F 盘可用 100,458,786,816 B。发行目录和 ZIP 使用项目继承 ACL，不带私有临时目录权限。

## 便携包组件扁平布局与根目录入口（2026-09-28，r15）

负责人为本聊天；目标是在 r11 的干净布局上去掉 `app/`、`runtime/`、`browsers/`、`postgres/` 下唯一的版本号目录，并提供根目录启动入口。组件精确 ID 继续记录在 `current.json` 和完整 manifest 中，原有 r10/r11 候选保持原样。新候选、发布输入和独立验收夹具预计新增峰值约 4 GiB，使用已锁定的 .NET、Python、Chromium、PostgreSQL 缓存，不新增依赖；构建与测试产物分别归入 `.tmp/build/portable-acceptance/<构建标识>/`、`.tmp/tests/portable-acceptance/<构建标识>/` 和 `.tmp/tests/portable-layout-r12|r14/`，候选放 `launcher/dist/`。开始前 F 盘可用约 109 GiB，`.tmp` 可读下限 16,801,306,287 B 且有 7 处旧目录枚举失败，均高于低磁盘暂停线。成功的可重建暂存由本任务收尾；保留的候选和验收证据在 2026-10-04 复核，不自动删除旧候选、共享缓存、状态型夹具或业务数据。

用户进一步确认根目录需要类似 `uninstall.exe` 的入口，并选择默认保留 `data/` 和 `backups/`，完全删除作为单独选项且再次确认。最终候选为 `launcher/dist/portable-acceptance-20260928-beta-layout-uninstall-r15/`，ZIP 为同名 `.zip`，389,034,909 B，SHA-256 `594da8549e0c571ee5e28193e924673318a5ffc8a342e9b1628e8136f1f21775`。展开后 8,076 文件 / 986,399,668 B（其中 manifest payload 8,074）；包根为六个组件目录、`AiGoofish.exe`、`uninstall.exe` 和两个 JSON 清单，无版本号空转目录、根层 DLL 或 PDB。两个根入口各 162,304 B，复用 `launcher/` 的自包含 .NET 文件。r12–r14 是中间候选，均未覆盖、删除或当作最终交付。

验证：r15 独立发布扫描 `PASS`、`findings=[]`；组装时 ZIP 成员、CRC、大小和 SHA-256 均校验；Launcher Core、HTTP、Python 生命周期、Windows 进程、UI Smoke、启动诊断和端口 UI 测试通过；r15 实包 Host→PostgreSQL `BUNDLE_ACCEPTANCE=PASS`。卸载助手只在任务自建合成目录做四项行为测试：默认保留数据库/备份、完全删除、未知文件拒绝、路径穿越拒绝，均通过；没有用真实用户数据试删。r14 中文路径完整解压副本扫描 `PASS`，根启动 EXE `--verify-package` 退出 0，正常启动显示 Launcher 窗口且未创建 `data/`；根卸载 EXE 显示“卸载闲鱼监控便携版”窗口，测试中关闭进程，没有选择删除。r15 本体根启动入口验证退出 0，卸载窗口标题可见且未选择删除；包根、两个 EXE 和 ZIP 的 ACL 均继承项目权限，无私有 DACL。仍需干净 Windows、r15 中文路径的完整原生交互和带真实数据的卸载人工验收，r15 状态继续为 `preview-integration`，不是公开发行放行。

阶段盘点 `.tmp` 可读下限 24,333,714,944 B / 159,533 文件，7 处历史 PostgreSQL 夹具目录拒绝枚举；F 盘可用 104,978,628,608 B，未触及低磁盘暂停线。r14 中文路径验收副本在 `.tmp/tests/portable-layout-r14/中文 解压/`，约 986,399,864 B，保留至 2026-10-04 复核。任务自建、可重建的 `.tmp/tests/portable-layout-r12/` 约 1,097,938,777 B，首次清理被自动审批审查拒绝，理由是缺少针对该准确路径的确认；用户随后明确批准后，重新核验路径边界、重解析点与在用进程，精确删除该目录，复核路径不存在。r15 构建和验收后 `.tmp` 可读下限 24,804,018,594 B / 152,103 文件，历史 7 处仍无法枚举；F 盘可用 103,020,535,808 B。其它历史候选、共享缓存、状态型夹具和用户数据未清理。本轮实际暂存峰值超过原先约 4 GiB 估算，便携任务容量特批适用；登记占用不代表自动清理已启用。

## 1.1.0.0-beta 规整包本地候选（2026-09-27，r11）

负责人为本聊天；目标是从当前源码生成 Windows x64 本地候选，将 Launcher 的自包含文件集中到 `launcher/` 并从发布目录排除 PDB。复用已锁定的 .NET、Python、Chromium、PostgreSQL 缓存，不新增依赖、不覆盖旧包。新建可重建构建及测试资料位于 `.tmp/build/launcher-refactor/full/`、`.tmp/build/portable-acceptance/<构建标识>/`、`.tmp/tests/portable-acceptance/<构建标识>/` 和 `.tmp/build/portable-bundle/`；新候选独立放在 `launcher/dist/`。预计新增峰值约 5 GiB，按便携任务容量特批执行，仍遵守低磁盘暂停。开始前 F 盘可用 120,383,082,496 B；`.tmp` 已知可读部分至少 13.96 GiB，7 处旧目录枚举失败。构建证据和候选于 2026-10-04 复核，不按期限自动删除；本轮只收尾自身可重建且无引用的暂存，不触碰历史候选、共享缓存或用户数据。

结果：构建标识 `20260927-231551-18664bc8b751`，Launcher Release 自包含发布为 222 文件 / 111,551,495 B，0 个 PDB；`--verify-package` 返回 `1.1.0.0-beta`、Windows x64、非开发构建。新候选 `launcher/dist/portable-acceptance-20260927-beta-tidy-r11/` 为 8,074 文件 / 986,356,942 B；包根仅有 `app/`、`browsers/`、`launcher/`、`postgres/`、`runtime/`、`third-party-notices/`、`current.json`、`bundle-manifest.json`，无根层 DLL 或包内 PDB。`current.json` 应用身份 `app-V1.1.0.0-beta-d5746e317b16`，Launcher 目录 `launcher/`，状态仍为 `preview-integration`。ZIP `launcher/dist/portable-acceptance-20260927-beta-tidy-r11.zip` 为 389,479,376 B，SHA-256 `7b4faeeb4233c271c73d1ac4cf47f67dbf626be21fb289003140b8ded820fa38`。组装器已核对 ZIP 成员、CRC、大小和哈希，独立扫描 `PASS`、`findings=[]`。根目录、`current.json`、Launcher EXE 和 ZIP 均继承项目权限，没有私有 DACL。

验收：Python 打包测试及扫描测试通过；C# Core、HTTP、Python 生命周期、Windows 进程、UI Smoke、启动诊断和端口 UI 检查通过；新包真实 Host→PostgreSQL 验收 `BUNDLE_ACCEPTANCE=PASS`，结束后包内路径进程数为 0。解压夹具 `.tmp/tests/portable-layout-r11/中文 解压/` 共 8,074 文件，独立扫描 `PASS`；从其中 `launcher/` 直接运行无参数 EXE，启动 journal 记录一次 `PackageVerification/Completed`、零次 `Failed`，没有创建 `data/`，本轮 Launcher 进程已退出。这是隐藏窗口的原生进程预检，不等于 Explorer 双击或界面交互验收。夹具约 1 GiB，可重建，负责人为本聊天，保留至 2026-10-04 复核。本轮尚未执行干净 Windows、完整账号业务流程、DPI、备份恢复稳定性和 Docker 回归，因此 r11 仍只是本地预览候选，不代表公开发行放行。阶段盘点 `.tmp` 可读下限 15,814,949,345 B / 132,121 文件，旧目录枚举失败仍为 7 处；F 盘可用 118,057,091,072 B。新建的 `.tmp/build/portable-acceptance/20260927-231551-18664bc8b751` 构建证据约 785.2 MiB，`.tmp/tests/portable-acceptance/20260927-231551-18664bc8b751` 测试证据约 0.7 MiB，保留至 2026-10-04 复核。打包器自己的临时 stage 已收尾；`.tmp/build/portable-bundle/` 仅见旧的 `acceptance-frozen-20260923-p1-pl7x7p3c`，本轮不清理。

最终盘点：ZIP 指纹不变，包根仍为 6 个目录和 2 个清单文件；候选及解压夹具路径下受管进程数为 0。`.tmp` 可读下限 16,801,306,287 B，7 处旧目录仍无法枚举，F 盘可用 117,053,165,568 B。Git 改动仅涉及布局、发布流程、对应测试和文档；`git diff --check` 通过，改动文件均为 UTF-8 无 BOM。历史候选、共享缓存及带状态的旧夹具未清理。

## r10 历史 beta 基线（2026-09-27）

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
