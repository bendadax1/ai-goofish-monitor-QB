# 项目计划（PLAN）

> 本文是**阶段计划、里程碑、风险登记与验收标准**的收敛入口。
> 架构与数据模型见 `DESIGN.md`；未尽事宜见根目录 `TODO.md`；
> 专项实施细节留在各自文档，本文只引用不复制。

建立日期：2026-10-02。基线版本：`V1.1.0.0-beta`。
本文档建立时，计划散落在 `PORTABLE_LAUNCHER_PLAN.md`、`UPSTREAM_UPGRADE_PLAN.md`、
`PORTABLE_ACCEPTANCE_STATUS.md`；本文收敛**主线**，专项文档继续作为其自身的实施基线。

---

## 1. 当前状态

| 项 | 状态 |
| --- | --- |
| 版本 | `V1.1.0.0-beta`（2026-09-27） |
| 已有发行 | Docker（公开镜像）、Windows 便携 ZIP（r10 验收基线） |
| 源码 vs 制品 | **源码领先于冻结包**，见风险 R1 |
| 许可证 | MIT（保留，见 §5） |

**已具备**：监控抓取、三维加权推荐、贝叶斯样本闭环、多用户 RBAC、
本地/PostgreSQL 双存储、8 渠道通知、Windows 便携 Launcher、Docker 部署。

**已建立工程保障**（2026-10-02 新增）：
- GitHub Actions CI：`ubuntu-latest` + Python 3.13 跑单元回归，跳过需真实
  PostgreSQL / Windows 原生运行时的用例（公开仓库免费不限分钟）。
- `tests/_ci_guard.py`：环境守卫，CI 无 DSN 时整类跳过，本机行为不变。

---

## 2. 阶段计划

### M1 — 工程基线与可回归（进行中）

| 目标 | 状态 |
| --- | --- |
| CI 跑通单元回归 | ✅ 已建立（`632bd31`） |
| 修复阻断性缺陷 | ✅ 已修 3 项（见 §3） |
| 锁定根 `requirements.txt` | ⬜ 待做（见 §4 R3） |
| CI 覆盖 PostgreSQL 用例 | ⬜ 待做 |
| 项目连续性三件套 | ✅ `TODO.md` / `DESIGN.md` / `PLAN.md` 已建立 |

### M2 — 便携版放行（未完成）

门槛（来自 `PORTABLE_LAUNCHER_PLAN.md` 与 `RELEASE_1.1.0.0-beta.md`）：

- ⬜ 无开发工具的干净 Windows 全流程
- ⬜ 完整闲鱼登录 / 抓取 / AI / 通知业务验收
- ⬜ 原生 DPI 矩阵与 Explorer 托盘
- ⬜ 最终包的备份恢复稳定性（曾出现独立串行验收失败，根因未完全确认）
- ⬜ Docker 在冻结源码上的回归
- ⬜ 从当前源码重新发布、生成清单与 ZIP、扫描与启动烟测，记录新指纹

### M3 — 上游吸收（部分完成）

范围与契约见 `UPSTREAM_UPGRADE_PLAN.md` §4。已按 B1/B2 分批推进；
**B2b 已重新施工但未完成整批验收**。便携新装候选为 schema v2，
正式实例未迁移，仍按各自既有结构运行。

### M4 — 产品能力扩展（未开始）

用户于 2026-10-02 提出的新需求，已登记在 `TODO.md`，需先出方案：

- **N1 任务「自动购买」选项** — 涉及真实资金与平台合规，须先定边界
- **N2 提示词与打分权重的对话式调参** — 前置：先配好 LLM 与闲鱼账号
- **N3 系统设置 / 模型管理按角色隐藏** — 前端隐藏 + 后端校验

---

## 3. 本基线已修复的缺陷

| 缺陷 | 影响 | 提交 |
| --- | --- | --- |
| `ai_handler.py` 导入期 `sys.stdout.detach()` | 销毁宿主流对象，pytest / Launcher 均受影响；改为就地 `reconfigure` | `6a21463` |
| passport「快速进入」确认页漏判 | 搜索页 30s 超时、0 商品；新增 `_wait_for_passport_redirect` | `d95c20e` |
| 抓取与登录浏览器身份不一致 | 跨身份触发确认页；统一为桌面身份 | `d6e193f` |

详见各提交信息与 `TODO.md` 已完结条目。

---

## 4. 风险登记

| ID | 风险 | 影响 | 应对 |
| --- | --- | --- | --- |
| **R1** | 源码领先于冻结制品 | 验收基线不唯一，`PORTABLE_ACCEPTANCE_STATUS.md` 多处标注「旧 r16 ZIP 未覆盖」 | 指定「哪份 ZIP / 哪个 commit 是当前验收对象」并记录指纹；正式分发前从当前源码重新发布 |
| **R2** | 备份恢复稳定性根因未确认 | beta 的备份恢复不能作为唯一数据保障 | 不覆盖生产实例、不导入唯一业务数据副本；补独立稳定性验收 |
| **R3** | 根 `requirements.txt` 未锁版本 | 会解析到 `openai 3.x + httpx2`，与 `httpx_compat`（适配 `httpx 0.28.1`）不兼容 | 已决定升级 `httpx_compat` 适配 `httpx2`（见 `TODO.md`） |
| **R4** | 闲鱼侧风控与前端改版 | 类名/接口随时变更，抓取可能失效 | 选择器尽量用语义定位；已有确认页兜底；本机实测验证而非只跑单测 |
| **R5** | 抓取 / 自动购买的平台合规 | 自动化操作可能触发封号；自动购买涉真实资金 | 不擅自改抓取频率与账号策略；N1 先出风控方案再施工 |
| **R6** | 真实账号与业务数据保护 | 测试可能污染真实数据 | 开发验收用合成数据与隔离实例；真实抓取/付费 AI/通知需另行授权 |

---

## 5. 决策记录（主线）

| ID | 决策 |
| --- | --- |
| D-license | 保留 MIT。2026-09 起暂停 GPL 切换讨论，不再作为执行指令或阻塞项。 |
| D-launcher | Launcher 用 C# / Avalonia / 自包含 .NET；首期仅 Windows x64；Docker 路线继续维护。 |
| D-storage | 首选 PostgreSQL，不新增 SQLite；保留本地文件后端。 |
| D-dual-release | Windows 便携包与 Docker 是同一业务后端的两种发行方式，不互相替代。 |
| D-scope | 项目定位为闲鱼监控与 AI 推荐；内容生产定位已删除。 |

完整决策表见 `PORTABLE_LAUNCHER_PLAN.md` §1.1（D01–D19）。

---

## 6. 验收标准与证据

### 6.1 单元回归

- 命令：`pytest tests`（CI 中自动排除需真实 PG / Windows 原生 / .NET 的用例）
- 当前基线：**475 passed / 6 skipped / 0 failed**（2026-10-02，Python 3.12 本机）
- 守卫：`tests/_ci_guard.py`（`needs_postgres` / `needs_windows` / `needs_frozen_launcher_dist`）

### 6.2 业务链路

以真实账号 + 真实浏览器实测为准，不以单测代替：

- 搜索页导航 → 搜索接口 200
- 商品解析与去重
- 卖家信息采集
- AI 分析通过结构校验（`criteria_analysis` 必备字段）
- 三维加权评分产出

2026-10-02 实测：桌面身份下确认页不再触发，搜索响应 200，33 个商品，
AI 分析成功，综合推荐分正常产出。

### 6.3 便携版

以 `PORTABLE_ACCEPTANCE_STATUS.md` 与 `PORTABLE_LAUNCHER_PLAN.md` 为准。
**注意**：离屏 Avalonia/Skia 截图不代表原生 Win32/DPI 或发行包验收。

### 6.4 证据边界

- 单测通过 ≠ 业务可用；需真实链路实测。
- 本机实测 ≠ 干净 Windows / 冻结制品验收。
- 历史候选包与诊断夹具按 `REPOSITORY_HYGIENE.md` 管理，不因过期自动删除。

---

## 7. 文档维护约定

- 完成一项待办：**先从 `TODO.md` 删除，再随代码变更一起提交**。
- 新发现的未尽事宜一律追加到 `TODO.md`，不只留在会话里。
- 阶段里程碑变化更新本文；架构变化更新 `DESIGN.md`。
- 专项契约变化更新各自文档，本文只引用不复制。
