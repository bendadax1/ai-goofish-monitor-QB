# 架构设计（DESIGN）

> 本文是**架构与数据模型**的收敛入口。阶段计划、里程碑和验收标准见 `PLAN.md`；
> 未尽事宜见根目录 `TODO.md`。专项契约不搬进本文，只在对应小节引用其原文：
> 便携版启动/HTTP 契约见 `PORTABLE_BACKEND_CONTRACT.md`，备份见 `PORTABLE_BACKUP_CONTRACT.md`，
> 打包见 `PORTABLE_BUNDLE_CONTRACT.md`，启动器决策见 `PORTABLE_LAUNCHER_PLAN.md`。

建立日期：2026-10-02。基线版本：`V1.1.0.0-beta`。

---

## 1. 系统定位与边界

闲鱼（Goofish）二手商品**监控 + AI 推荐**系统。核心链路：

```
监控抓取(Playwright) → 条件筛选 → 贝叶斯先验 + AI 视觉/画像 → 三维加权评分
   → 结果存储(local / PostgreSQL) → 通知推送(8 渠道)
```

**属于本系统的**：任务调度、抓取、筛选、AI 分析、推荐评分、结果管理、通知、多用户与权限。

**不属于**：内容生产/审核/成品上传（历史误置定位，已删除，见 `PORTABLE_LAUNCHER_PLAN.md` D12）。

**不可变边界**（改动需显式授权，见 `UPSTREAM_UPGRADE_PLAN.md` 第 3 节）：
抓取周期、页数上限、并发默认值、账号绑定/轮换、通知触发条件、去重语义、
贝叶斯样本与推荐权重。

---

## 2. 技术栈

| 层 | 选型 | 版本锚点 |
| --- | --- | --- |
| 语言 | Python | 3.10+；便携内置 3.13.15 |
| Web | FastAPI + Uvicorn | `fastapi==0.127.0` / `uvicorn==0.40.0` |
| 调度 | APScheduler（AsyncIOScheduler） | `apscheduler==3.11.2`，时区 `Asia/Shanghai` |
| 抓取 | Playwright（Chromium） | `playwright==1.57.0` |
| AI | `openai` SDK | `openai==2.14.0`（**须配 `httpx==0.28.1`**，见 §6.3） |
| 存储 | SQLAlchemy 2.0 + psycopg2 | `sqlalchemy==2.0.45` |
| 加密 | cryptography（Fernet）+ bcrypt | `cryptography==46.0.3` / `bcrypt==4.0.1` |
| 前端 | Jinja2 模板 + 原生 JS/CSS | 响应式（PC/平板/移动端） |
| Launcher | C# / Avalonia / 自包含 .NET | SDK `10.0.401`，仅 Windows x64 |

便携链路顶层依赖锁定在 `scripts/portable/requirements-python.in`
（含哈希锁 `requirements-python.lock.txt`）。根 `requirements.txt` 的锁定情况见 `PLAN.md`。

---

## 3. 代码结构与模块职责

```
web_server.py            兼容入口，转发到 src/web/main.py
collector.py             单任务采集入口（拼接 AI prompt 后调用 fetch_xianyu）
login.py                 自动登录，产出账号快照到 state/<name>.json

src/
  web/                   FastAPI 应用与路由（每个 manager 一个 APIRouter）
    main.py              app 装配、lifespan、认证中间件、静态挂载
    auth.py              会话/Cookie、多用户判定、local 与 postgres 双模式认证
    scheduler.py         APScheduler 装载与重载
    task_manager.py      任务 CRUD、进程启停、criteria/参考文件虚拟路径
    result_manager.py    结果查询、删除
    settings_manager.py  系统/通知/AI 设置（含登录程序启动）
    ai_manager.py        AI 配置管理
    ai_health.py         健康快照与主动测试（读写分离）
    account_manager.py   闲鱼账号池
    bayes_api.py         贝叶斯参数管理
    user_manager.py      用户、用户组、RBAC
    notification_manager(_v2).py  通知配置与测试
    log_manager.py       运行日志读取与导出
  storage/               统一存储抽象
    interface.py         StorageInterface 抽象基类（全部契约）
    local_adapter.py     本地文件后端
    postgres_adapter.py  PostgreSQL 后端
    models.py            SQLAlchemy 模型
    migration.py         文件 → PG 迁移工具
    utils.py             口令校验、主密钥
  portable/              便携版运行时（18 模块）
    app_paths.py         程序/数据/缓存路径解析
    context.py           便携环境冻结（模式、DSN）
    schema.py / schema_catalog.py / schema_fingerprint.py   schema 版本与指纹
    provision.py         新实例准备（建库/角色/表）
    seeds.py             系统种子数据
    web_runtime.py       便携业务 Web 运行时与门禁
    launcher_pairing.py  Launcher ↔ 后端配对与控制令牌
    backup_*.py          备份/归档/恢复（契约见 PORTABLE_BACKUP_CONTRACT.md）
    maintenance.py       维护入口与就绪探测
    upstream_ddl.py / upstream_migration.py   上游 schema 迁移
  notifier/              通知分发
    channels.py          8 渠道实现
    config.py            渠道配置与旧配置迁移
  feedback/              样本闭环
    feature_extractor.py 商品卡特征提取
    sample_manager.py    样本管理与回灌

launcher/src/
  AiGoofish.Launcher.App              Avalonia UI、托盘、诊断导出
  AiGoofish.Launcher.Core             LauncherCoordinator（启动步骤/维护）
  AiGoofish.Launcher.Platform.Windows 平台适配（进程、路径、ACL）

src/scraper.py           抓取核心（fetch_xianyu）
src/ai_handler.py        AI 调用、响应解析、通知编排
src/bayes.py             贝叶斯先验计算
src/recommendation_scorer.py  三维加权融合评分
src/parsers.py           闲鱼接口响应解析
src/search_requests.py   搜索请求识别与分页
src/httpx_compat.py      NO_PROXY IPv6 CIDR 实例级兼容
prompts/                 基础提示词与贝叶斯参数
criteria/ requirement/   任务 AI 标准（运行时生成，gitignore）
templates/ static/       前端模板与静态资源
```

---

## 4. 运行时拓扑

### 4.1 三种运行形态

| 形态 | 入口 | 存储 | 认证 |
| --- | --- | --- | --- |
| **本地模式** | `python web_server.py` | 本地文件 | `.env` 的 `WEB_USERNAME`/`WEB_PASSWORD`（可空=免登录） |
| **服务器/Docker** | `docker-compose.yaml` | PostgreSQL | 多用户，强制认证 |
| **Windows 便携** | `Launcher.exe` | 内置 PostgreSQL | 多用户 + Launcher 控制令牌 |

三者为**同一业务后端的三种发行方式**，不直接共用正在写入的 PGDATA。

### 4.2 进程模型（Web 形态）

```
FastAPI 主进程
  ├─ APScheduler（任务装载与 cron 触发）
  ├─ 每个运行中任务一个 collector 子进程（可并发多任务）
  └─ 自动登录时一个 login 子进程（短期）
```

任务以**子进程**隔离运行，主进程通过 `fetcher_processes` 字典跟踪 PID，
便于单任务终止与僵尸清理。便携模式下另有**排空（drain）**语义：先暂停调度器
新派发，等在用 worker 收敛，超时才恢复，避免强杀导致数据不一致。

### 4.3 路径与环境（便携模式关键约束）

- `PORTABLE_MODE` 在 `src/config.py` 导入期**冻结**：项目 `.env` 无法切换执行模式或 DSN。
- `PORTABLE_APP_ENV_ALLOWED_KEYS` 白名单——仅日志等级、`RUN_HEADLESS` 等
  非敏感偏好可写入 `app.env`；**数据库、凭据、监听端口、服务端点一律由 Launcher 进程管理**。
- 便携业务 Web 必须经 `portable_web.py` 启动；legacy 入口无法通过导入获得该状态。

---

## 5. 数据模型

### 5.1 存储抽象

`src/storage/interface.py` 的 `StorageInterface` 定义全部契约（用户、用户组、
任务、结果、账号、通知配置、AI 配置、贝叶斯配置等），两个适配器实现：

- `LocalStorageAdapter` — 文件后端，对应根目录 `jsonl/` `state/` `criteria/` 等。
- `PostgresAdapter` — PostgreSQL 后端，`create_tables` 建表并初始化基础资源。

`get_storage()` 返回单例。**便携模式固定 postgres，失败不静默回退 JSON。**

### 5.2 关键实体（概念级）

| 实体 | 说明 |
| --- | --- |
| User / UserGroup | 用户与用户组，承载 RBAC |
| Role | 超级管理员 / 管理员 / 操作员 / 游客（默认四级，支持自定义分组） |
| Task | 监控任务：关键词、价格区间、筛选条件、cron、绑定账号、AI 标准文件 |
| Account | 闲鱼账号池：Cookie 快照、环境指纹、风控历史 |
| Result | 监控结果：商品信息、卖家信息、AI 分析、贝叶斯预计算 |
| ApiConfig | AI 连接配置，敏感字段 Fernet 加密 |
| NotificationConfig | 通知渠道配置 |
| BayesProfile | 贝叶斯权重与参数 |

### 5.3 多用户隔离与唯一性

- **所有新数据由服务端确认 owner**；不信任客户端 `owner_id`，不以缺失 owner 回退全局或跨用户聚合。
- `monitoring_results` 当前按 `(owner_id, item_id)` **唯一**，不能直接当作多次价格观察历史；
  新增价格历史不得破坏该唯一性（见 `UPSTREAM_UPGRADE_PLAN.md` §2.1）。
- 本地模式（无 owner）须单独明确存储空间，不与数据库多用户语义混用。

### 5.4 便携 schema 版本

- `src/portable/schema.py` 当前声明 schema 版本；新装候选为 **v2**。
- **已有库升级不能用 `create_all` 代替**，必须走版本化迁移。
- `backup_restore.py` 校验固定表集合与版本。新增持久化能力必须先解决
  版本迁移、权限和备份恢复的联动。

---

## 6. 关键接口与契约

### 6.1 HTTP

业务路由由 `src/web/main.py` 装配，各 manager 提供 `APIRouter`。认证中间件
`check_auth` 依据 `is_auth_required()` 决定是否校验会话。静态资源在便携模式下
从程序目录挂载，业务上传（头像）单独挂在数据目录，避免混入不可变组件。

### 6.2 通知渠道（8 种）

企业微信（群机器人 / 应用）、钉钉（加签 + ActionCard）、Telegram、
Ntfy、Gotify、Bark、自定义 Webhook。各渠道有独立开关与分渠道代理开关。
历史 `NTFY_TOPIC_URL` 配置在启动时自动迁移（`migrate_legacy_ntfy_config`）。

### 6.3 AI 调用链

```
scraper.fetch_xianyu
  → build_bayes_precalc（贝叶斯先验，失败不影响主流程）
  → get_ai_analysis(final_record, image_paths, prompt_text, owner_id, bayes_profile)
       ├─ 响应解析 → 结构校验（criteria_analysis 必备字段等）
       ├─ 失败重试（有预算上限，超限即终止任务）
       └─ ai_parameter_fallback（可选参数不支持时回退）
  → recommendation_scorer 三维加权融合
```

**兼容约束**：`src/httpx_compat.py` 适配 **`httpx 0.28.1`** 的私有扩展点
（`_get_proxy_map` / `_transport_for_url`），用于实例级 NO_PROXY IPv6 CIDR 旁路。
升级 httpx 必须重跑 `tests/test_upstream_httpx_compat.py` 的路由与 SDK 默认值回归。

**抓取浏览器身份**：`scraper._default_context_options()` 与 `login.py` 统一为**桌面身份**
（Windows UA / 1366×768 / `is_mobile=False`）。跨身份复用会触发闲鱼「快速进入」确认页；
`_wait_for_passport_redirect` 仍保留为兜底。

### 6.4 便携后端契约

见 `PORTABLE_BACKEND_CONTRACT.md`：独立维护入口、固定白名单模块、
loopback 限制、令牌与 DSN 传递方式、结构化错误。

---

## 7. 安全边界

- 敏感字段（API 密钥、Cookie）使用 **Fernet** 加密存储；`ENCRYPTION_MASTER_KEY` 生产必须替换默认值。
- 登录口令用 **bcrypt**；超级管理员默认口令有风险提示。
- 会话 Cookie 有签名与过期（`SESSION_EXPIRE_SECONDS`，默认 7 天）。
- 便携模式：Launcher 令牌 ≥32 字节随机；数据库仅经 `GOOFISH_PORTABLE_DATABASE_URL` 传入；
  **令牌与 DSN 不得出现在命令行、JSON 或日志中**。
- 文件路径边界：Prompt/criteria 虚拟路径有解析与边界校验（`src/file_safety.py`）。
- 产物权限：普通构建与交付产物继承项目 ACL；**数据库口令、控制令牌等凭据单独保护**，
  两者不得混为一谈（见根 `AGENTS.md` 第 7 条）。

---

## 8. 部署拓扑

### 8.1 Docker

`docker-compose.yaml`（用户侧）与 `docker-compose.server.yaml`（服务器侧）。
挂载 `.env`、`logs`、`jsonl`、`criteria`、`requirement`；
`prompts` 与 `state` 默认不挂载（注释形式保留）。
镜像 `banbanzhige/ai-goofish-monitor-qb`，容器内 8000，主机映射 8001。

### 8.2 Windows 便携

ZIP 内置：自包含 Launcher、独立 PostgreSQL、锁定 Python 与依赖、配套 Chromium。
普通启动**不执行 pip / git pull / 浏览器下载**。组件与用户数据隔离，
数据默认跟随便携目录，更新不得覆盖真实配置、登录态或业务文件。

---

## 9. 与本文相关的文档

| 文档 | 内容 |
| --- | --- |
| `PLAN.md` | 阶段计划、里程碑、风险登记、验收标准 |
| `TODO.md` | 未尽事宜清单 |
| `PORTABLE_LAUNCHER_PLAN.md` | 便携版与 Launcher 决策基线 |
| `PORTABLE_BACKEND_CONTRACT.md` | 便携维护后端启动/HTTP 契约 |
| `PORTABLE_BACKUP_CONTRACT.md` | 备份与恢复契约 |
| `PORTABLE_BUNDLE_CONTRACT.md` | 打包产物契约 |
| `UPSTREAM_UPGRADE_PLAN.md` | 上游吸收范围与不可变边界 |
| `RELEASE_1.1.0.0-beta.md` | 当前版本内容、已知限制与制品身份 |
| `PORTABLE_ACCEPTANCE_STATUS.md` | 便携版逐批验收记录 |
| `REPOSITORY_HYGIENE.md` | 临时产物与清理边界（根目录） |
| `AGENTS.md` | 项目规范与工具规则（根目录） |
