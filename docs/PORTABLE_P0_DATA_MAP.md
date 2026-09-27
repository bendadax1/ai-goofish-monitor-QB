# Windows 便携版 P0 数据与路径地图

> 盘点日期：2026-09-12
>
> 范围：主要持久文件、配置来源、程序资源和 Python 子进程路径。本文是接线清单，不重复总体 Launcher 规划，也不表示便携版已经实现。

## 1. 盘点结论与边界

当前代码同时使用三种定位方式：相对当前工作目录、基于 `__file__` 的仓库根、调用方传入路径。相对工作目录仍是多数 Web、抓取和日志路径的事实来源，因此只改变 Launcher 的工作目录不能构成可靠的数据隔离。

本批新增 `src/runtime_paths.py`，只定义显式 `program_root`、`data_root`、`cache_root` 及三类拼接入口，尚未接入任何业务消费者。源码布局默认把三根映射到模块所在仓库根，仅是新模块的兼容默认；不能据此声称所有旧的 cwd 行为已经兼容。

本次未读取真实 `.env`、账号状态、结果或任务文件的内容，也未启动服务。仓库根可见 `.env`、`config.json`、`criteria/`、`jsonl/`、`logs/`、`requirement/`、`state/`、`task_stats/`，这里只以源码消费者判断用途。

## 2. 程序只读资源

| 当前路径 | 当前消费者与证据 | 边界判断 |
| --- | --- | --- |
| `src/`、根入口脚本 | `Dockerfile:57,65` 将代码复制到 `/app` 并运行 `web_server.py`；`web_server.py:23` 启动 Web 应用 | 应归 `program_root`，更新时可替换 |
| `templates/`、`static/`（不含 `static/avatars/`） | `src/web/main.py:127-132` 以 cwd 相对路径挂载静态目录和模板 | 应归 `program_root`；当前仍依赖 cwd |
| `images/logo/`、`images/readme/`、`images/Example/`、`images/login-bg.png` | Web 通过 `src/web/main.py:129` 暴露整个 `images/`；模板引用品牌/登录资源 | 随包只读资源应归 `program_root`，但 `images/` 同时混有运行时任务图片，不能整目录按只读处理 |
| `prompts/guide/` | `src/prompt_utils.py:17` 以相对路径读取权重指南；`src/web/settings_manager.py:863` 读取 Bayes 指南 | 应归 `program_root` 的只读指南；当前依赖 cwd |
| `prompts/*.txt`、`prompts/bayes/*.json`、`criteria/*.txt`、`requirement/*.txt` | `src/storage/postgres_adapter.py:258-307,411-424` 从仓库根扫描并初始化 PG 系统资源；`src/user_file_store.py:19-36` 又把这些目录作为共享可编辑文件 | **混合边界**：既是随包默认种子又是现有共享数据，拆分前不能在更新时直接覆盖 |

## 3. PostgreSQL 之外的主要资产

| 当前路径 | 内容/敏感性 | 主要消费者与事实证据 | 初步归根（未接线） |
| --- | --- | --- | --- |
| `.env` | 全局运行配置及数据库、AI、通知、Web 登录等密钥；高度敏感 | `src/config.py:10,36-39,367-429` 自动加载、读取、重写并同步进程环境；`src/notifier/config.py:184-253` 还会迁移旧通知字段 | `data_root/config`；不能打进 ZIP 或日志 |
| `config.json` | local 模式任务配置 | `src/storage/local_adapter.py:52-67` 基于仓库根读写；`src/web/task_manager.py:30,213-228` 与 `scheduler.py:19,296-324` 仍用 cwd 相对路径 | `data_root`；PG 便携主路径仍需作为旧数据导入源识别 |
| `state/` | 账号/Cookie 状态、活动账号、RBAC 文件、用户文件及运行时任务副本，可能敏感 | `src/web/account_manager.py:22-23`、`src/web/auth.py:33`、`src/user_file_store.py:16-36`、`src/web/scheduler.py:20,108-116` | 持久账号和用户文件归 `data_root/state`；`runtime_task_configs/` 是可清理运行态，但清理策略须另审 |
| `jsonl/` | local 结果；PG 写失败时可选回退结果 | `src/storage/local_adapter.py:43-44,274-458`；`src/utils.py:142-215`；`src/scraper.py:934-953` | `data_root/results`，不是缓存 |
| `criteria/`、`requirement/`、`prompts/` | 共享 Prompt/标准/Bayes 配置及多用户读取回退；可能含用户输入 | `src/user_file_store.py:16-141`；`src/storage/migration.py:486-518` | 用户修改部分归 `data_root/assets`；随包默认资源需先定义复制/版本规则 |
| `static/avatars/` | 用户上传头像 | `src/web/user_manager.py:1599,1663,1690` 直接写在程序静态目录 | `data_root/assets/avatars`，更新不能覆盖 |
| `logs/`、`logs/tasks/`、`logs/exports/` | 运行日志与导出包，可能含诊断信息 | `src/logging_config.py:28-60,156-223`；`src/log_exporter.py:22-23,97`；Web 子进程也写 `logs/fetcher.log` | `data_root/logs`；备份包含项和保留细则待专项确定 |
| `task_stats/` | 受保护的任务运行记录，运行中在用 | `src/scraper.py:628-668` | 先按 `data_root/state/task_stats` 保护；最终映射与生命周期待专项确认 |
| `images/task_images_*` | 下载的任务图片；与正式图片共用父目录，是否仍被业务引用未核验 | `src/ai_handler.py:202-240` | 仅经生产者/消费者核验为可重建且无业务引用的中间件，才可迁入 `cache_root`；当前不得泛化清理 |

PG 模式并不消除文件资产：PG 存储用户、任务、结果、API/通知/平台账号等表，但系统种子仍来自文件，头像、日志、部分用户文件和运行时文件仍在 PG 外。`src/storage/__init__.py:50-80` 在首次取得 PG 适配器时还会执行 `create_tables()` 及系统资源初始化，不能把健康探测当作无副作用的路径检查。

Docker 的现有持久化范围也不一致：`docker-compose.yaml:9-15` 挂载 `.env/logs/jsonl/criteria/requirement`，而 `docker-compose.server.yaml:12-14,37-39` 挂载 `.env/state/postgres_data`。后续路径接线必须分别回归，不能把 Windows 目录或 Launcher 约定写死进容器。

## 4. 配置来源与优先级

| 优先级/分支 | 当前行为 | 证据与风险 |
| --- | --- | --- |
| 任务子进程用户覆盖 | PG 多用户任务把用户 AI/代理配置注入 `GOOFISH_*`；相关 accessor 先读这些键 | `src/web/task_manager.py:148-189,354-375`；`src/config.py:71-103`。只覆盖列明字段，不是通用配置层 |
| `.env` 对进程环境的覆盖 | 导入 `src.config` 时调用 `load_dotenv(override=True)`；多数 accessor 随后读 `os.getenv` | `src/config.py:9-10,41-45`。发现规则与显式 `dotenv_values(".env")`/保存 cwd 路径混用，便携接线前须统一 |
| PG 用户配置或 local 全局配置 | `STORAGE_BACKEND=postgres` 时 AI/通知走用户 DB 配置；local 模式调用 `save_env_settings` 写 `.env` | `src/web/settings_manager.py:347-350,971-1029,1099-1213` |
| 存储后端 | `STORAGE_BACKEND` 默认 `local`；PG 还要求 `DATABASE_URL`，失败不会静默回退 local | `src/config.py:105-111`；`src/storage/__init__.py:48-76` |
| 用户文件读取 | 有 owner 时先 `state/user_files/<owner>/...`，不存在再读共享目录；写入有 owner 时落用户目录 | `src/user_file_store.py:65-119` |

密钥位置至少包括 `.env` 中的 `DATABASE_URL`、`ENCRYPTION_MASTER_KEY`、AI Key、通知 Token/Webhook 和 local Web 密码，以及 PG 内加密的用户 API/通知/平台账号配置。便携凭据保护与备份恢复尚未实现，本批不改变任何密钥存储。

## 5. 子进程与可执行路径

| 启动点 | 当前解析 | 便携接线要求 |
| --- | --- | --- |
| 开发批处理 | `start_web_server.bat:145` 使用 `venv\Scripts\activate.bat`，随后依赖 cwd 和系统 `python` | 保留开发入口；便携 Launcher 另传内置解释器和绝对脚本路径 |
| 手动/定时采集 | `src/web/task_manager.py:354-375`、`scheduler.py:168-203` 使用 `sys.executable` + 相对 `collector.py`，未传 `cwd` | 解释器、脚本、日志、运行时配置均由统一根解析并显式传入 |
| 登录进程 | `src/web/settings_manager.py:937-951` 检查并执行相对 `login.py`，未传 `cwd` | 改为程序根绝对脚本；状态输出路径单独走数据根 |
| 抓取浏览器 | `login.py:357-363`、`src/scraper.py:966-972` 本机优先 channel `msedge/chrome`，Docker 用 Playwright 浏览器 | 便携模式以后显式选择随包浏览器；本批未改浏览器行为 |
| Docker Web | `Dockerfile:23,65` 固定 `/app` 工作目录并执行 `python web_server.py` | 保持现有 Docker 路线，不依赖 Windows 盘符或 .NET |

## 6. 后续小批次接线顺序

1. 只读程序资源：`templates/static`、只读图片、指南和 PG 系统种子；先消除 Web/种子初始化对 cwd 的依赖。
2. 子进程：明确内置 Python、`collector.py/login.py`、日志和运行时任务配置的绝对路径及环境白名单。
3. 基础持久文件：`.env` 的替代/兼容入口、`config.json`、`state/`、用户文件；先写兼容测试，不自动搬迁真实文件。
4. 结果和运行资产：`jsonl`、头像、日志、`task_stats`、任务图片；分别定义备份、保留与运行中占用规则。
5. Docker 回归后再切换业务入口默认路径；旧目录只作为显式导入来源，不自动删除或静默合并。

## 7. 未核验盲区

- 未逐字段核验真实 `.env`、`config.json`、账号、结果和用户文件，也未验证这些文件与当前 PG 数据的一致性。
- 未穷举前端动态 URL、第三方库缓存、Playwright 浏览器下载目录及所有异常分支产生的文件。
- 未验证符号链接/junction、只读目录、网络盘、移动盘符、并发占用、长路径或杀毒软件拦截；`RuntimePaths` 不是安全路径沙箱。
- 未确定日志应进入哪一类备份及其保留细则、默认种子被用户修改后的升级合并规则、头像迁移兼容路由。
- 未启动 Docker、Web、PG、抓取、AI 或通知，也未验证发布 ZIP 内容、备份恢复或旧数据迁移。
