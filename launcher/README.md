# Windows x64 便携 Launcher

2026-09-27：基于 r10 已通过的本机启动验收提交 `1.1.0.0-beta` 源码版本。统一 Runtime、独立准备步骤、持久启动诊断和增量开发入口已落地；完整发行矩阵尚未完成。使用方式见 [无打包开发与诊断](../docs/LAUNCHER_DEVELOPMENT.md)，当前验收范围与限制见 [beta 版本说明](../docs/RELEASE_1.1.0.0-beta.md)，分阶段实现见 [重构执行记录](../docs/LAUNCHER_REFACTOR_EXECUTION.md)。旧 r10 ZIP 不会因源码版本更新而自动变成新版制品。

本目录包含 Avalonia Launcher、独立编排 Core、Windows 实例/进程适配，以及 PostgreSQL、Python Web、备份恢复和端口管理的测试入口。默认入口现已接入真实本地组件，`--simulation` 才是隔离的模拟演练。下文保留了早期 P0-B1～B5 的阶段性验收记录；当前能力与缺口以 [便携版验收状态](../docs/PORTABLE_ACCEPTANCE_STATUS.md) 和 [本地验收指南](../docs/PORTABLE_LOCAL_ACCEPTANCE.md) 为准。集成预览候选不等于可公开发行版本。

## 明确边界

- 默认真实模式先核验发行清单与实例状态，再由用户手动启动内置 PostgreSQL 和 Python Web；已有运行进程只在持久身份和就绪契约通过后接管。模拟模式不启动真实组件。
- 隔离验收必须使用全新的合成数据根；不得把预览包指向既有业务数据、配置、登录态、任务或通知记录。
- `AiGoofish.Launcher.Core` 不引用 Avalonia 或 Windows API；平台编排通过 Windows 适配层实现，尚未交付其他桌面平台。
- 正常启动按注册顺序执行，停止与回收按依赖逆序执行。若上一层停止失败，不继续盲停其下游数据库。

## 工程与依赖锁定

| 工程 | 目标 | 直接 NuGet 依赖 |
| --- | --- | --- |
| `AiGoofish.Launcher.Core` | `net10.0` | 无 |
| `AiGoofish.Launcher.App` | `net10.0` / `win-x64` | `Avalonia.Desktop` 12.1.2、`Avalonia.Themes.Fluent` 12.1.2 |
| `AiGoofish.Launcher.Core.Tests` | `net10.0` Console | 无测试框架；仅引用 Core |
| `AiGoofish.Launcher.Ui.Smoke` | `net10.0` Console | 测试用途 `Avalonia.Headless` 12.1.2；无测试框架 |
| `AiGoofish.Launcher.Platform.Windows` | `net10.0-windows` | 无；引用 Core，承载 Windows 实例锁与受管进程适配 |
| `AiGoofish.Launcher.Platform.Windows.Tests` | `net10.0-windows` Console | 无测试框架；同一 EXE 的 `--child` 模式作为真实辅助进程 |
| `AiGoofish.Launcher.Postgres.Tests` | `net10.0-windows` Console | 无测试框架；只在显式开关下使用锁定的 PostgreSQL 17.11 组件 |

版本依据：Avalonia 12 桌面目标支持 .NET 8 及以上，官方建议新项目优先 .NET 10；12.1.2 是本批核验时的稳定补丁版。参考：[Avalonia 12 变更说明](https://docs.avaloniaui.net/docs/avalonia12-breaking-changes)、[平台支持](https://docs.avaloniaui.net/docs/supported-platforms)、[Avalonia.Desktop 12.1.2](https://www.nuget.org/packages/Avalonia.Desktop/12.1.2)、[Avalonia.Themes.Fluent 12.1.2](https://www.nuget.org/packages/Avalonia.Themes.Fluent/12.1.2)。

`launcher/global.json` 精确锁定 SDK 10.0.401；Microsoft 10.0 发布元数据对应运行时 10.0.12、LTS 支持至 2028-11-14。`scripts/portable/install-dotnet-sdk.ps1` 下载官方 win-x64 ZIP（300,608,304 B），按官方 SHA-512 校验后解压到 `.tmp/dependencies/p0-b2/dotnet-sdk-10.0.401/`，不全局安装、不修改系统 `PATH`。这次 P0 工具链通过不等于已经确认公开发行的最低 Windows 构建；发行前仍需干净机和原生窗口验证。

历史阻塞：最初的 `net8.0` 原型在 SDK 8.0.406 上因 Avalonia 12.1.2 分析器要求 Roslyn 4.14 而报 `CS9057`。迁移到官方推荐的 .NET 10 后保留分析器并已正常编译，没有通过关闭分析器、降级 Avalonia 或增加临时编译器包绕过。

还原生成的 `packages.lock.json` 记录直接和传递依赖的版本及内容哈希。普通启动过程不会执行 NuGet、pip、git 或浏览器下载。

## 隔离构建与空间

两份 PowerShell 脚本在任何正常 `dotnet` 调用前隔离 `DOTNET_CLI_HOME`、NuGet 包/HTTP 缓存、`TEMP/TMP`、`DOTNET_ROOT`、多级 SDK 查找以及 .NET/Avalonia 遥测，并在成功或失败后恢复调用方变量。构建脚本默认使用绝对路径 `.tmp/dependencies/p0-b2/dotnet-sdk-10.0.401/dotnet.exe`，可用 `-DotnetPath <绝对路径>` 显式覆盖，但版本仍必须为 10.0.401。

在仓库根目录复跑：

```powershell
./scripts/portable/install-dotnet-sdk.ps1
./scripts/portable/build-launcher-prototype.ps1
./scripts/portable/build-launcher-prototype.ps1 -PostgresIntegration
```

默认命令执行锁定还原、Release 全 solution 构建、Core Console 验收、Windows 真实辅助进程验收、UI Headless 验收、win-x64 自包含发布和已发布 EXE 诊断入口检查。`-PostgresIntegration` 额外运行真实 PostgreSQL 生命周期集成测试；默认读取 `.tmp/dependencies/portable-pg/postgresql-17.11-3-windows-x64`，也可用 `-PostgresRoot <绝对路径>` 指向同版本完整组件。仅复跑 Core 可加 `-CoreOnly`。只有主动更新依赖时使用 `-RefreshLock`，审查并提交 `packages.lock.json` 后恢复默认锁定模式。`launcher/NuGet.Config` 清除用户级 feed，只允许官方 nuget.org；新缓存写入 P0-B2 目录，P0-B1 包缓存只作 fallback 复用，不重复复制已完整依赖。

历史 P0 原型输出（以下目录可能已按授权清理，不代表当前可用制品）：

- `launcher/dist/launcher-p0/`：225 个文件、213,599,406 B；实际入口为 `AiGoofish.Launcher.App.exe`。`--verify-package` 不初始化窗口或服务，已从发布后的 EXE 输出 `.NET 10.0.12`、进程/系统 `X64`、Windows 和 `SIMULATION_ONLY=True` 验证结果；目录包含 `runtimeconfig.json`、`coreclr.dll`、`hostfxr.dll`。
- `launcher/dist/launcher-p0-verification/`：离屏 PNG 与已发布 EXE 的 stdout/stderr 验证记录。stderr 为 0 B。
- `.tmp/dependencies/p0-b2/`：1,286,956,456 B、5,794 个文件，含已校验 SDK ZIP/解压 SDK、新运行时包和 HTTP 缓存。
- `.tmp/build/p0-b2/`：1,412,331,616 B、620 个文件，为可重建 Release 构建/测试中间件。
- `.tmp/dependencies/portable-pg/`：482,426,508 B、1,566 个文件，含显式 B5 集成测试复用的 PostgreSQL 17.11-3 win-x64 锁定组件；保留至 2026-09-20 供验收复跑，过期或替换版本后仍须核验引用并取得确认，不自动删除。
- `.tmp/dependencies/p0-b1/`：1,754,637,218 B、1,177 个文件，作为 fallback 复用；此前中断 RID 还原留下的不完整条目未获清理授权，未删除或覆盖。
- `F:\ai-goofish-monitor\github\ai-goofish-monitor-QB\.tmp\build\p0-b1\` 复核后尝试清理，仅删除本批自有 774,507 B 可重建产物；剩余 9,683,798 B、32 个文件。清理在 `temp/VBCSCompiler/AnalyzerAssemblyLoader/.../System.Text.RegularExpressions.Generator.resources.dll` 遇到访问拒绝，按可能在用处理，未提权强删或终止共享编译器。剩余项为失败诊断/编译器中间件，保留至 2026-09-19；编译器不再引用且重新核验准确路径后人工清理。
- `F:\ai-goofish-monitor\github\ai-goofish-monitor-QB\.tmp\diagnostics\p0-b1\validation.md` 为小于 2 KiB 的脱敏验收摘要，保留至 2026-09-19，无排障引用后人工清理。
- B5 收尾只读盘点时仓库共享 `.tmp` 共 6,348,118,651 B、26,880 个文件（含其他并行任务目录）；F 盘可用 55,503,323,136 / 1,024,191,361,024 B（51.69 GiB、5.42%），未触发 10 GiB/5% 暂停线。`.tmp/tests/portable-launcher-pg/` 与 `.tmp/tests/launcher-process/` 子项均为 0；本任务自有主要目录仍为上列 P0-B1/P0-B2 与 portable-pg 依赖、构建目录。用户已取消本任务容量上限；上述缓存仍不自动清理，任何后续清理需重新核验准确路径、引用、进程和授权。
- 隔离疏漏记录：为诊断早期残留 build server，曾有一次手工 `dotnet build-server shutdown` 在脚本外调用，显示首次运行欢迎文案，可能触及 C 盘默认 CLI home；未读取业务数据或密钥。此后脚本已改为任何 `dotnet` 前先隔离全部相关环境。
- 文档记录的是人工纪律，不表示仓库已实现自动限额、低磁盘保护或定时清理。

## Core 契约与控制台验收

Core 通过 `ILauncherComponent` 注入组件，保存每次状态观察结果。关键行为：

- 同一时刻只允许一个启停操作；启动中或运行中重复启动抛出明确错误。
- 组件的 `StartAsync` 即使抛错，也会重新查询所有组件，避免漏掉“实际已运行但启动返回失败”的组件。
- 取消只在安全点中断；不可中断步骤使用 `CancellationToken.None` 完成后再进入逆序回收。
- 停止、失败回收或取消回收出现异常时保留实际组件状态，不假报 `Stopped`。
- 上游组件停止失败时中止停止序列，保留下游依赖；后续可排障并重试。
- 状态/日志观察者逐个隔离，UI 订阅异常不会破坏 Core 的启动或收尾。
- Core 日志环形缓存上限 500 条，UI 集合上限 300 条。

2026-09-12 使用隔离 SDK 10.0.401 锁定复跑：全 solution Release 构建 0 个警告、0 个错误，12/12 项 Core Console 验收通过。覆盖正常状态序列及逆序停止、并发/运行中重复启动、启动失败后的剩余组件、安全点取消、停止失败的依赖保护、启动/停止后实际状态确认、运行或预检期间释放、并发关闭串行、观察者异常隔离和日志上限。它是无额外测试框架的 P0 驱动，不替代后续真实进程、中文/空格路径、普通用户、端口冲突、PG 初始化、实例身份或就绪检查验收。

## Windows 实例与进程安全层（P0-B3）

`AiGoofish.Launcher.Platform.Windows` 是有界 Windows 适配，不接入默认主按钮，也不启动 PostgreSQL、Python 或业务后端：

- `InstanceDataRootLease` 只接受绝对数据根；创建前逐级拒绝已有重解析点，以 `.launcher.instance.lock` 的 `FileShare.None` 持有排他锁。锁文件保留持久 `instance_id`，会话重开不换 ID；已有空/损坏元数据明确拒绝，不删除锁文件充当修复。
- `OwnedProcessLaunchSpec` 必须给出存在的绝对 EXE 与工作目录。启动固定使用 `ArgumentList`、`UseShellExecute=false`、`CreateNoWindow=true` 和重定向输出；不拼 Shell 命令。
- 子进程环境先清空，再合并必要系统白名单、显式类型化变量和 Launcher 的 instance/run 标识。调用方必须把凭据变量标成 `Sensitive=true`；日志不记录参数正文或环境值。
- `OwnedProcessIdentity` 保存 PID、UTC 启动时间、绝对 EXE、数据根、持久 `instance_id` 与本次随机 `run_id`。刷新、停止和重连前核对这些信息；无法读取足够证据时返回 `Unknown`，不操作未知进程。
- 正常停止只调用可注入 `IProcessStopProtocol`。协议请求和等待退出共享一个总时限；超时/取消/协议失败不强杀、不假报 `Exited`，保留 identity 和管理记录。仍挂起的协议不会被重复发送。
- 管理器释放会停止输出读取并释放自己持有的 `Process` 句柄，但不终止子进程，也不释放外部共享 lease。重连停止协议必须是独立控制通道；测试使用仅含随机 `run_id` 的本机命名管道，不依赖原 stdin。
- stdout/stderr 读取与 Core 日志都有条数上限；单条也有限长。敏感值跨读取块时先完整脱敏再安全截断，超长或未换行输出使用固定省略说明，避免无界内存增长或部分密钥外泄。

同一 Console EXE 的 `--child` 模式只用于测试。构建脚本把每轮案例放入 `.tmp/tests/launcher-process/<guid>/`，最终对每个跟踪身份复核 PID、启动时间和 EXE，通过独立正常停止协议退出并确认进程已结束，再验证目录边界和重解析点后删除本轮目录；无法确认退出就保留目录并令测试失败，不按进程名查杀。2026-09-12 验收为 9/9：启动/正常停止、启动早退、停止超时后重试、挂起协议总时限与防重复、排他锁/持久 ID/空或损坏元数据、启动取消后保留归属、日志容量/长度/脱敏、启动等待中的安全异步释放、释放后按身份重连。验收后测试根子项、测试子进程及隔离 SDK build server 均为 0。

B3 通用进程层仍未接入已发布 App 或默认模拟主按钮；B5 在下一节增加 PostgreSQL 专用适配。Python/Chromium、业务维护门控、HTTP token 就绪和强制终止仍未实现，不因通用辅助进程通过就视为完成。

## Windows 实例基础凭据保护（P0-B4）

`WindowsInstanceSecretsStore` 复用已持有的 `InstanceDataRootLease`，只保护 Launcher 创建本地实例所需的五个固定字段：PostgreSQL 初始化管理员密码、应用数据库密码、探测数据库密码、`ENCRYPTION_MASTER_KEY` 和 `SECRET_KEY`。每个字段由 `RandomNumberGenerator` 独立生成至少 32 字节后编码；不包含用户 AI Key、通知密钥、平台登录态或运行会话控制 token。

- API 只有显式 `CreateNew(lease, postgresDataPath)` 与 `Load(lease)`，没有无条件 `LoadOrCreate`。调用方必须传入未来实例配置确定的唯一 PGDATA；本层不扫描、创建或修改其他 PostgreSQL 目录。
- `CreateNew` 只接受当前实例数据根内的绝对 PGDATA，逐级核验重解析点和访问错误。PGDATA 已非空、是文件、无法确认或越界时拒绝生成；已有凭据文件也拒绝覆盖。
- 文件固定为 `<instanceRoot>/config/instance-secrets.dpapi`。外层明文头只含格式版本、持久 `instance_id` 和 Base64 密文；受保护载荷使用固定 schema，并再次校验版本、ID、五个字段及每字段解码长度。
- 使用 Windows 当前用户 DPAPI；不设置 `CRYPTPROTECT_LOCAL_MACHINE`，同时设置 `CRYPTPROTECT_UI_FORBIDDEN` 且 prompt 为 null。可选熵只绑定固定域标签与持久 `instance_id`，不绑定绝对路径，因此同一 Windows 用户在所有进程完全停止后移动整个数据根仍可读取。
- DPAPI 输入/输出均限制为 64 KiB。输入使用本地分配，DPAPI 输出按官方要求调用 `LocalFree`；解密输出和托管明文字节在 `finally` 清零。错误只公开固定操作和 Win32 数值错误码，不写入原数据、密钥、环境、参数或日志。
- 写入使用同目录唯一暂存文件、`WriteThrough`、`Flush(true)` 和不覆盖目标的同卷 `File.Move`。提交失败清理本次唯一暂存；清理也失败时同时报告两项固定上下文，不删除已有目标。
- 空、缺字段、未知字段、超限、篡改、错误实例、DPAPI 解密失败或错误 Windows 用户都按失败关闭处理，绝不自动覆盖或重新生成。DPAPI 通常只允许同一登录用户在同一机器解密；本批不实现或宣传跨用户、跨电脑恢复。[CryptProtectData](https://learn.microsoft.com/en-us/windows/win32/api/dpapi/nf-dpapi-cryptprotectdata)、[CryptUnprotectData](https://learn.microsoft.com/en-us/windows/win32/api/dpapi/nf-dpapi-cryptunprotectdata)、[LocalFree](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-localfree)

2026-09-12 在当前 Windows 用户下完成真实 DPAPI 测试；Windows suite 共 15/15 通过，其中 B4 六项覆盖：往返与所有生成文件无明文、全停移动数据根、密文篡改/null/缺密钥/超限、错误实例和已有文件不变、非空或越界 PGDATA、原子提交失败的唯一暂存清理。没有创建测试用户、修改系统保护策略或尝试真实业务凭据。`InstanceSecrets` 使用托管字符串向后续组件提供值，无法承诺运行时托管堆中的字符串可立即擦除；本批保证的是不持久化明文、不输出和尽量清零临时字节缓冲。

密码加密导出、受保护恢复、换用户/换电脑迁移及密钥轮换仍未实现，不能把同用户目录移动测试表述为完整便携恢复能力。

## PostgreSQL 生命周期适配（P0-B5）

`WindowsPostgresComponent` 复用 B3 的实例租约与进程身份、B4 的初始化管理员密码，只管理当前实例数据根内、配置明确指定的唯一 PGDATA。它不扫描系统 PostgreSQL，不注册 Windows 服务，不修改系统 `PATH`，也不需要 UAC。

- 构造时核验 PostgreSQL 安装根、`postgres.exe`、`initdb.exe`、`pg_ctl.exe`、`pg_isready.exe` 和锁定的 17.11 版本。所有命令使用 `ArgumentList`、无 Shell、受限环境和有界 stdout/stderr；口令不进入 argv、日志或普通权限临时文件。
- 首次初始化只接受空 PGDATA。`initdb` 启动前先原子写入 `<instanceRoot>/config/postgres-initialization.json`；启动回调补记辅助进程 PID、UTC 启动时间和 EXE。失败、取消或超时后保留该记录并拒绝自动重试，避免对不确定目录重建或补标记。
- `initdb` 口令文件创建时即关闭 ACL 继承并只授权当前 Windows 用户，成功或失败均尝试清理。只有命令成功、`PG_VERSION` 为 17 且实例/集群 marker 已原子提交后，才删除初始化中记录。
- 启动直接管理 `postgres.exe`。`pg_isready` 返回可连接后仍会复核 B3 受管身份和 `postmaster.pid`，全部一致才把持久运行状态写为 `Running`；端口被其他监听者占用时自身进程早退，不接管或停止原监听者。
- 重连与停止前同时核验实例、集群、版本、PGDATA、端口、B3 身份和 `postmaster.pid`。正常停止只使用 `pg_ctl stop -m smart -W`；超时、取消、协议失败或归属未知时不强杀、不使用 immediate 模式、不删除 `postmaster.pid`，也不假报已停止。
- `pg_isready` 结果只表示本机 TCP transport 是否接受连接，不证明认证 SQL 可执行、目标 schema 已创建、迁移已完成或业务可用。本批没有连接数据库执行 SQL，也没有实现 schema、角色/数据库创建和 business-ready 探测。

2026-09-13 使用 `./scripts/portable/build-launcher-prototype.ps1 -PostgresIntegration` 完成一次显式验收：全 solution Release 构建 0 个警告、0 个错误；Core 12/12、Windows 15/15、PostgreSQL 5/5、UI Headless 和自包含发布入口均通过。PostgreSQL 用例覆盖真实集群初始化、直属 `postgres.exe` 启动、释放后按身份重连、smart stop、停止后新组件读取 `Stopped`、非空无 marker 拒绝、初始化中断记录拒绝自动重试且原记录不变、端口冲突不接管，以及 initdb 临时口令文件 ACL。相关命令语义参考 PostgreSQL 17 官方文档：[initdb](https://www.postgresql.org/docs/17/app-initdb.html)、[postgres](https://www.postgresql.org/docs/17/app-postgres.html)、[pg_ctl](https://www.postgresql.org/docs/17/app-pg-ctl.html)、[pg_isready](https://www.postgresql.org/docs/17/app-pg-isready.html)。

## UI 说明

界面沿用规划中的浅灰背景、白色卡片、蓝色主操作、深色只读日志视口和固定底部操作区。使用系统中文字体，不引入在线字体、图标包、MVVM 库或商业控件。异步操作禁用重复提交并显示阶段；按钮采用语义控件、保留默认键盘焦点，状态同时用文字说明，不只依赖颜色。

本批不启动可见 GUI。`AiGoofish.Launcher.Ui.Smoke` 使用 Avalonia Headless + Skia 加载真实 XAML，验证主按钮/取消按钮初始状态、点击启动到模拟 `Running`、点击停止到 `Stopped`、启动中关闭等待安全收尾，并有明确超时。它生成：

- `ui-smoke-1120x760.png`：模拟运行中，验证标准窗口布局与日志。
- `ui-smoke-920x640.png`：模拟已停止，验证规划最小窗口；组件与日志区域可滚动，底部操作保持可见。

Headless 只验证控件树、布局、绑定、交互路径和离屏像素输出；PNG 非空不等于视觉通过。两张图已人工检查无关键溢出，但中文字形只是离屏后端表现。本批未验证 Win32 原生窗口、1366×768 工作区、多档 DPI、键盘焦点可见性、屏幕阅读器或长时资源。界面固定为首期浅色主题，完整深色主题后置；这些仍是 P1 验收项。
