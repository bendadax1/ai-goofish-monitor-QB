# TODO

未尽事宜清单。完成一项即从本文件删除该项，并随该项的代码变更一起
`git commit` + `git push`。新发现的未尽事宜一律追加到这里，不要只留在会话里。

---

## 待办

- [x] ~~**补齐项目连续性三件套**~~ **已完成**（2026-10-02）：
      `TODO.md`（本文件）、`docs/PLAN.md`（阶段计划/里程碑/风险登记/验收标准）、
      `docs/DESIGN.md`（架构/数据模型/存储抽象/运行时/部署拓扑）均已建立。
      专项契约（便携后端、备份、打包、Launcher 决策、上游吸收）保持独立，
      由 `DESIGN.md` 与 `PLAN.md` 引用而非复制。
- [ ] **为 CI 增加 PostgreSQL service job**：当前 CI 跳过全部需要真实 PG 的用例
      （`tests/portable_*_pg_*.py` 等）。用 `services: postgres:16` + `DATABASE_URL` 复跑这一批，
      并在 `tests/_ci_guard.needs_postgres` 断言上生效。
- [x] ~~**锁定 `requirements.txt` 版本 / 适配 `httpx2`**~~ **已完成**（2026-10-02，`dd600b0`）。
      按用户决定升级 `httpx_compat` 兼容两代 HTTPX，而非锁回 `openai 2.x`：
      - `src/httpx_compat.py` 新增兼容导入层，按 **openai 的实际依赖元数据**
        决定用 `httpx` 还是 `httpx2`（两者可能共存，例如被其他依赖间接引入）。
        选错库会让混入的客户端与 SDK 内部客户端不是同一库，patch 静默失效。
      - `_parse_no_proxy_network` 兼容两代 pattern：`all://[2001:db8::/32]`（httpx）
        与 `all://[2001:db8::]/32`（httpx2）。
      - `src/config.py`、`src/prompt_utils.py`、`src/web/ai_health.py` 改用兼容导入。
      - 验证：同一套用例在 **openai 2.14.0 + httpx 0.28.1** 与
        **openai 3.22.1 + httpx2 2.13.1（并混入 0.28.1 覆盖共存）** 下各跑一遍，
        均 491 passed / 6 skipped / 0 failed。
      **仍待做**：根 `requirements.txt` 的精确版本锁定本身（D07），本次只解决兼容性。
- [ ] **`ai_handler.py` 编码修复已改，需回归**：原先 `sys.stdout.detach()` 在导入期销毁宿主
      流对象（pytest / Launcher 均受影响），已改为就地 `reconfigure(encoding="utf-8")`。
      **部分已验（2026-10-02）**：`python web_server.py` 直接运行与 CI 用例均正常，
      中文日志无乱码。**仍待验**：Launcher 捕获子进程日志场景（需在便携构建中确认）。
- [ ] **明确发行验收基线**：`docs/PORTABLE_ACCEPTANCE_STATUS.md` 多处标注「旧 r16 ZIP 未覆盖」，
      源码领先于冻结包。需指定「哪份 ZIP / 哪个 commit 是当前验收对象」，并记录指纹。
- [ ] **过发布放行门槛**：干净 Windows、原生 DPI/Explorer 托盘、最终包备份恢复、Docker 冻结后复跑。
- [ ] **CI 覆盖 Launcher（.NET）**：当前 CI 不构建 `launcher/`。待评估在 `windows-latest` 上
      跑 `scripts/portable/build-launcher-prototype.ps1 -NoPublish` 的成本与必要性。
- [x] ~~**修复自动登录（`login.py`）**：当前完全失效。~~ **此条结论有误，已撤回。**
      更正：`login.py` 使用桌面上下文（`is_mobile=False`、1366×768），实测首页停在
      `goofish.com/`，`div.nick--RyNYtDXM` 与 `#alibaba-login-box` 均 count=1，点击后
      弹出含「手机扫码安全登录」的登录框。原实现有效，用户实测可自动获取账号。
      此前判定为失效是因为诊断脚本误用了 `scraper._default_context_options()`（移动端 UA），
      移动版登录页才没有扫码入口且类名为 0。误加的 `login.py` 改动已回滚。
- [ ] **登录与抓取使用不同的浏览器身份（设计不一致，待评估）**：`login.py` 用桌面上下文
      采集账号快照，而 `src/scraper.py` 的搜索流程用移动端上下文
      （`_default_context_options()` 为 `is_mobile=True` + Android UA）。跨身份复用会
      触发闲鱼「快速进入」确认页。当前已在 scraper 侧加了确认页处理（`_wait_for_passport_redirect`），
      但两者身份是否应统一需要产品决策：统一为桌面可减少确认页，统一为移动端可能更贴近其风控基线。
- [x] ~~**`extra_http_headers` 手动指定 `Accept-Encoding` 会掩盖真实错误**~~
      **已按用户决定做防御性过滤**（保留原判断「实测在有效 Cookie 下无差异」的更正）：
      实测 4 种组合（移动端/桌面 × 有效/无效 Cookie）含与不含该头行为一致，此前归因有误；
      但仍从 `src/search_requests.build_extra_headers` 的排除集加入 `accept-encoding`，
      理由改为 Playwright 官方建议由浏览器自管压缩，属防御性收敛而非缺陷修复。

## 新需求（2026-10-02 用户提出，均未开始）

- [ ] **N1. 创建任务时增加「是否自动购买」选项**：任务配置新增字段，并在任务创建/编辑界面提供开关。
      需先明确：自动购买属于对平台的写操作，涉及支付、账号安全与合规，须定义边界
      （是否需要二次确认、金额上限、失败与风控回滚、是否仅下单不付款）。**不要在没有明确授权与
      风控设计前实现真实下单**；先出方案再施工。
- [ ] **N2. 提示词模板与打分权重的对话式调参**：内置初始 prompt 与打分参数，用户以自然语言描述
      「哪里效果不好」，LLM 理解后自动调整参数，循环迭代直至满意。
      前置条件（用户已明确）：**必须先配置好 LLM 与闲鱼账号**，否则该功能不可用。
      需设计：调试会话的状态与历史保存、参数版本与回滚、调整是否写入任务/全局配置、
      调用成本控制（避免无上限的自动重试）、以及调整后如何用真实或样本数据回放验证效果。
- [ ] **N3. 系统设置与模型管理按角色隐藏**：现有 RBAC 为超级管理员/管理员/操作员/游客四级
      （见 `docs/DESIGN.md` 与 `src/web/user_manager.py`、`src/web/auth.py`）。
      需把「系统设置」「模型管理」纳入页面/接口级权限控制，前端隐藏 + 后端校验同时生效，
      不能只做前端隐藏。
