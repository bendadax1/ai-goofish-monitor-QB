# TODO

未尽事宜清单。完成一项即从本文件删除该项，并随该项的代码变更一起
`git commit` + `git push`。新发现的未尽事宜一律追加到这里，不要只留在会话里。

---

## 待办

- [ ] **补齐项目连续性三件套**：`TODO.md`（本文件已建立）、`docs/PLAN.md`、`docs/DESIGN.md`。
      现状：计划散落在 `docs/PORTABLE_LAUNCHER_PLAN.md`、`docs/PORTABLE_ACCEPTANCE_STATUS.md`、
      `docs/UPSTREAM_UPGRADE_PLAN.md`。需把阶段计划/里程碑/风险登记/验收标准收敛到 `PLAN.md`，
      把架构、数据模型、存储抽象、Provider 接口、便携运行时路径、部署拓扑收敛到 `DESIGN.md`。
- [ ] **为 CI 增加 PostgreSQL service job**：当前 CI 跳过全部需要真实 PG 的用例
      （`tests/portable_*_pg_*.py` 等）。用 `services: postgres:16` + `DATABASE_URL` 复跑这一批，
      并在 `tests/_ci_guard.needs_postgres` 断言上生效。
- [ ] **锁定 `requirements.txt` 版本**：落实 `PORTABLE_LAUNCHER_PLAN.md` 决策 D07（版本锁定依赖）。
      便携链路已有 `scripts/portable/requirements-python.lock.txt`，CI 现按
      `scripts/portable/requirements-python.in` 安装以复现受支持组合；但仓库根
      `requirements.txt` 仍未锁，会解析到 `openai 3.x + httpx2`，与
      `src/httpx_compat.py`（适配 `httpx 0.28.1` 私有扩展点）不兼容。
      需决定：升级 `httpx_compat` 适配 `httpx2`，还是在根 requirements 锁定 `openai 2.x + httpx 0.28.1`。
- [ ] **`ai_handler.py` 编码修复已改，需回归**：原先 `sys.stdout.detach()` 在导入期销毁宿主
      流对象（pytest / Launcher 均受影响），已改为就地 `reconfigure(encoding="utf-8")`。
      需确认 Windows 控制台中文输出、Launcher 捕获日志、`python web_server.py` 直接运行均正常。
- [ ] **明确发行验收基线**：`docs/PORTABLE_ACCEPTANCE_STATUS.md` 多处标注「旧 r16 ZIP 未覆盖」，
      源码领先于冻结包。需指定「哪份 ZIP / 哪个 commit 是当前验收对象」，并记录指纹。
- [ ] **过发布放行门槛**：干净 Windows、原生 DPI/Explorer 托盘、最终包备份恢复、Docker 冻结后复跑。
- [ ] **CI 覆盖 Launcher（.NET）**：当前 CI 不构建 `launcher/`。待评估在 `windows-latest` 上
      跑 `scripts/portable/build-launcher-prototype.ps1 -NoPublish` 的成本与必要性。
