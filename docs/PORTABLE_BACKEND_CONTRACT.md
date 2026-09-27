# 便携版维护后端契约

> 状态：P0 有界实现，协议版本 1（2026-09-12）

`portable_server.py` 是 Launcher 专用的独立维护入口。它不导入或启动
`src.web.main`、业务 router、认证、调度器或存储适配器；不读取 `.env`，
不建表、不初始化用户、不迁移数据，也不触发抓取、AI 或通知。本入口完成
不代表常规业务 Web 已便携化。

## 启动契约

```text
<runtime>\\python.exe -B <app>\\scripts\\portable\\python-bootstrap.py \
  --app-root <absolute-app-path> \
  --target maintenance \
  -- \
  --mode maintenance \
  --instance-id <public-instance-id> \
  --program-root <absolute-path> \
  --data-root <absolute-path> \
  --port <1..65535>
```

- Python bootstrap 仅允许固定白名单：`maintenance`、`schema`、`provision`、`web`、`collector`、`login`；它拒绝任意模块、脚本或相对应用根。worker 调用层只允许 `collector` / `login`，不继承 Launcher 控制及初始化凭据。`python313._pth` 固定为 `python313.zip`、`.`、`site-packages` 和 `import site`，不加载用户或系统 site-packages。
- 入口只支持 `maintenance`；传入 `normal` 会明确失败，不会默默启动业务服务。
- 服务固定监听 `127.0.0.1`。路径参数必须是绝对路径，解析不依赖当前工作目录，入口不创建这些目录。
- Launcher 秘密仅由 `GOOFISH_LAUNCHER_TOKEN` 传入；数据库连接仅由
  `GOOFISH_PORTABLE_DATABASE_URL` 传入。两者不得出现在命令行、JSON 或日志中。
- Launcher 必须用密码学安全随机数生成至少 32 字节的 ASCII token。后端可校验格式和长度，不能从 token 文本证明随机性。
- DSN 必须明确包含 loopback `host`、单一合法 `port`、`dbname`、`user`
  和 `password`。`hostaddr` 如存在也必须是 loopback；非本机、多主机、
  `service` / `servicefile` 以及 `PGSERVICE` / `PGSERVICEFILE` 默认服务配置均被拒绝。
- 端口、模式、路径、token 或 DSN 配置错误会在 Uvicorn 启动前以固定、脱敏错误失败。

Uvicorn 负责标准的 `Ctrl+C` 处理及受保护控制请求后的正常退出。服务不注册
Windows 服务，不修改 PATH 或防火墙；普通停止不依赖 Windows 强制结束进程。

## HTTP 契约

本节 HTTP 为独立 maintenance 进程。正常业务入口为 `portable_web.py --mode normal
--instance-id <id> --port <port>`，通过 bootstrap 的 `web` 固定目标启动；普通业务采用
`GOOFISH_PORTABLE_DATABASE_URL` 的 app 角色，探测采用
`GOOFISH_PORTABLE_PROBE_DATABASE_URL` 的 probe 角色，两者须指向同一 hostaddr/port/dbname。
正常入口的本机状态要求独立控制令牌和实例 header；首次设置使用不同的
`GOOFISH_PORTABLE_SETUP_TOKEN`，不自动赋予控制令牌业务身份。

新实例的显式准备入口为 bootstrap `provision -- --pgdata <绝对路径> --instance-id <UUID>`。
它读取 `GOOFISH_PORTABLE_ADMIN_DATABASE_URL`（仅 postgres 库）、
`GOOFISH_PORTABLE_APP_DATABASE_PASSWORD` 和 `GOOFISH_PORTABLE_PROBE_DATABASE_PASSWORD`，
密码不进命令行。校验 C# 集群 marker 与服务器 data_directory 后，以独占锁创建固定
`aigoofish` 库、`aigoofish_app` / `aigoofish_probe` 角色及 schema 1。已有业务库或角色时拒绝，
不删库/改密码/自动补建；CREATE DATABASE 非事务，因此中断是需要诊断的初始化恢复状态，
不是普通重试。普通 Web 启动不执行该入口。

### `POST /internal/shutdown`

仅停止此独立维护进程。必须同时携带 Bearer 令牌和匹配的
`X-Goofish-Instance-Id`，拒绝含 `Origin` 的浏览器请求。错误令牌为 401，
实例/调用上下文不符为 403，控制回调不可用或失败为 503；成功为 202
`{"status":"shutdown_requested"}`。重复接受不重复调用退出回调。
202 只表示收到请求，Launcher 仍须等待并核验自己持有的进程确实退出。
它不授权业务 API、用户操作、数据库停机或任务强杀。

### `GET /health`

公开存活检查固定返回 HTTP 200：

```json
{"status":"alive"}
```

它不返回版本、实例、数据库或 schema 信息，也不是 authenticated
readiness。

### `GET /internal/ready`

请求必须携带 `Authorization: Bearer <GOOFISH_LAUNCHER_TOKEN>`。后端对 token
哈希执行常数时间比较。缺失或错误凭据返回 HTTP 401，且不调用数据库
probe。

认证后响应示例：

```json
{
  "protocol_version": 1,
  "instance_id": "portable-a1",
  "app_version": "V1.0.4.5",
  "observed_at": "2026-09-12T01:02:03Z",
  "mode": "maintenance",
  "ready": true,
  "database": {"status": "available"},
  "schema": {
    "status": "compatible",
    "version": 1,
    "supported_min": 1,
    "supported_max": 1
  },
  "failure_reason": null
}
```

`ready=true` 仅在数据库可连接且 schema 版本兼容时成立，并返回 HTTP
200。其他认证后状态返回 HTTP 503，失败原因仅使用以下固定分类：

| 数据库 | Schema | `failure_reason` | 含义 |
| --- | --- | --- | --- |
| `unavailable` | `unknown` | `database_unavailable` | 连接或基础查询失败 |
| `available` | `uninitialized` | `schema_uninitialized` | 版本表不存在或表为空 |
| `available` | `invalid` | `schema_invalid` | 不是恰好一行正整数版本 |
| `available` | `incompatible` | `schema_incompatible` | 版本不在支持范围 `1..1` |

schema 契约表为 `public.app_schema_version(version integer)`。Probe 先用
`to_regclass` 判断表是否存在，然后最多读取两行以验证“恰好一行”；不使用
`MAX()` 掩盖损坏数据。连接超时为 2 秒，语句超时为 2 秒，会话和事务均强制只读；
只执行 `SELECT`/元数据查询，结束后回滚并关闭短连接。原始驱动异常和 DSN
不进入响应或日志。

本服务没有 OpenAPI/交互文档、业务读写、数据库初始化、迁移或外部测试路由。

## 调用方责任和局限

Launcher 必须把预期的 `protocol_version`、`instance_id`、`app_version`、
数据库就绪和 schema 兼容同时纳入归属校验，不能因 `/health` 或端口可连就
认定是自己的可用实例。当前只实现版本查询；schema 初始化、迁移、锁、备份和
恢复是后续独立能力，不得由该 probe 暗中执行。

## P0 隔离验证证据（2026-09-12）

- 维护入口单元验证：`python -B -m unittest tests.test_portable_maintenance -v`，22 项通过。
- Windows x64 PostgreSQL 组件锁与准备：
  `scripts/portable/postgresql-win-x64.lock.json` 和
  `scripts/portable/prepare-postgres.ps1`。版本锁定 17.11 revision 3，来源是
  [PostgreSQL Windows 官方页](https://www.postgresql.org/download/windows/) 指向的
  [EDB 二进制 ZIP 页](https://www.enterprisedb.com/download-postgresql-binaries)。压缩包为
  341,325,378 bytes；本地锁定 SHA-256
  `4b8db0930c38f6ef845db919551dedda3b6b845aeb0927b3d79a6e8e9e4537cf`。
  EDB 未在该下载页提供此 ZIP 的发布者 SHA-256 或本批已验证签名，因此锁明确记录
  `verification=official_https_local_sha256` 且 `signature_verified=false`，不将本地哈希宣称为官方签名。
- 选择性 runtime 只包含 ZIP 的 `bin/`、`lib/`、`share/`、服务器许可和命令行
  第三方许可；1,565 个文件，141,101,130 bytes。未运行 installer、pgAdmin
  或 StackBuilder，未注册服务，完整 ZIP 只在 `.tmp/dependencies/portable-pg/` 作为可校验复用缓存保留。
- 真实隔离冒烟：`python -B -m tests.portable_pg_smoke`。在唯一
  `.tmp/tests/portable-pg/<id>` 内以当前普通 Windows 用户初始化 UTF8 / C locale
  集群，只监听 `127.0.0.1` 随机端口，本地和 host 认证均为 SCRAM-SHA-256。
  新建的低权限 probe 角色不是超级用户，不能建库/建角色/复制，默认事务只读。
  真实 SQL 先验证 `schema_uninitialized`，再创建一行版本 1 的测试 fixture
  验证 `compatible`；HTTP 链路得到 `401, 401, 503, 200`。本机 HTTP 客户端
  显式禁用环境代理，不向外部代理发送 Launcher token。成功后先停止维护
  HTTP 子进程，再按新建 PGDATA、PID、端口和启动时间归属执行有界
  `pg_ctl stop -m fast`，核验状态 3 后清理成功临时集群。这个 fast stop 策略只用于无业务数据的测试集群，不是未来生产停机策略。
- 内置 Python runtime：`scripts/portable/python-runtime.lock.json` 与
  `scripts/portable/prepare-python.ps1` 固定 Python 3.13.15 Windows x64 embeddable
  ZIP。Python.org 发布页列出该文件的 11,009,825 bytes 与 SHA-256
  `d1f04d990aee1253d8569e8e5104e30fa9f5fa830899f14843448872d936a2cf`；准备脚本复用
  已校验下载、拒绝解压路径穿越或链接，并使用已验证的 `uv 0.10.8` 将带 hash、仅 wheel 的
  `requirements-python.lock.txt` 安装到 runtime 自己的 `site-packages`，不改 PATH、全局
  site-packages 或 C 盘缓存。新鲜 staging runtime 还会写入 `python-runtime-manifest.json`，绑定
  ZIP SHA-256、requirements lock SHA-256、`._pth` SHA-256 和精确包版本集合；复用时重算这些
  身份，并校验每个 wheel 的 `RECORD` 文件、大小及 SHA-256。缺少该清单、锁已改变或包不完整时
  一律拒绝复用，不自动覆盖旧 runtime。本批最初生成的 5,726 文件、265,536,034 bytes runtime
  早于该 marker 机制，因此现在仅作为不可验证的隔离缓存保留，准备脚本会明确拒绝它；只有新鲜
  staging 安装完成并写入 marker 的 runtime 才可作为可复用发行输入。新 runtime 目录名同时包含
  Python ZIP SHA-256 与 requirements lock SHA-256 的组合身份短码，例如
  `python-3.13.15-<identity12>-windows-x64`。`prepare-python.ps1` 默认是构建准备命令：使用固定
  `https://pypi.org/simple` 索引、禁用 uv 隐式配置、`--only-binary :all:` 与 requirements hash 校验，
  因而可在没有既有缓存的新机器上下载锁定 wheel。显式传入 `-Offline` 时才只使用隔离 uv 缓存；
  两种模式都不触及普通用户启动流程、全局 Python/PIP 缓存或 PATH。
  验证器不只信任 marker：它逐项把 runtime 的每个原生 archive 文件（包括 `python.exe`、
  `python313.dll`、标准库 ZIP、DLL/PYD）和已验证 ZIP 内容逐字节比对，`._pth` 则单独按隔离
  契约核验。其中历史 runtime 的
  `site-packages` 为 5,692 个文件、244,198,922 bytes。
- 隔离验证：`python313._pth` 仅包含 `python313.zip`、`.`、`site-packages`、`import site`。
  内置解释器的 `sys.prefix` 和 `sys.base_prefix` 都是其 runtime 根，`sys.path` 仅有上述三个
  runtime 路径；在注入无效 `PYTHONPATH` 时，`fastapi`、`psycopg2`、`uvicorn` 与
  `playwright` 都从该 runtime 的 `site-packages` 导入。内置解释器直接运行
  `tests/test_portable_maintenance.py -v` 的 22 项验证通过。随后用系统测试驱动 PG fixture，
  通过 `--python-executable <runtime>\\python.exe --python-bootstrap <app>\\scripts\\portable\\python-bootstrap.py`
  令维护 HTTP 子进程实际走内置 bootstrap；真实 HTTP 链路再次得到 `401, 401, 503, 200`，
  测试 PGDATA 已停止并清理。
- 可复用的本批依赖缓存保留在 `.tmp/dependencies/portable-python/` 至 2026-09-19：总计
  819,571,150 bytes，包括已校验 ZIP 11,009,825 bytes、历史不可验证 runtime 265,536,034 bytes、
  新 runtime `python-3.13.15-e67c6b779c81-windows-x64` 265,542,788 bytes 和 uv wheel/cache
  277,482,503 bytes；`temp/` 为空。新 runtime 的 identity 为 `e67c6b779c81`，requirements lock
  SHA-256 为 `16c17453b50f817d9fc663ea6222bfad839aa268855eee7d648f5e09391e52df`。它们不是业务数据或
  用户配置，过期后仍须按仓库卫生守则先盘点、确认未被使用，不能由本说明自动删除。

未验证：常规业务 Web 便携化、真实业务 schema/用户、
真实业务数据、抓取、付费 AI、通知、备份/恢复、更新或 Docker 回归。
