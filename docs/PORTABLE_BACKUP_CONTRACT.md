# 便携业务备份接入契约

更新：2026-09-23。后端组合备份与隔离恢复演练已实现；Launcher Host 已有停写备份调用和显式 UI。新增恢复目标编排、目标用户 DPAPI handoff 调用、维护核对、活动实例 CAS 指针与显式确认状态机源码；只经过 fake/隔离进程验收，尚无真实 Host→PG 恢复演练或恢复 UI。

## 调用条件

`src/portable/backup_business.py:create_business_backup` 只供可信编排层调用，不暴露无认证 HTTP 路由或任意 Shell 输入。
调用方必须持有实例锁，正常停止 Web 和全部工作进程，同时保留已核验归属的 PG 运行以便导出；全程禁止其他路径启动写入者。
`verify_quiesced` 是强制回调，在数据库导出前后、文件复制后重核验实例锁与停写条件，发现失效必须抛错。空函数只可用于明确无写入者的单元 fixture，不是生产集成。

Launcher Host 当前以独占操作门先正常排空 Web，保留归属明确的 PG。固定 Python 入口通过私有 stdio 管道对三次 `verify_quiesced` 与 Host 往返；每次由 Host 复核实例锁、Web 已停、PG 原身份及传输状态。不经 argv、环境或日志发送备份口令、DSN、业务密钥。此为源码接线和假组件测试证据；真实 Host→PG 备份及 UI 尚未验收。

目标是数据根之外的新文件；不覆盖已有备份，不在线执行依赖安装。明文中间件放在当前 Windows 用户私有 ACL 目录；结束清理自有暂存，失败不删除原始数据。
现有组合备份会短暂写入明文 `recovery-keys.json` 到私有 staging，然后加密并清理；不能称为“密钥全程不落盘”。若 staging 清理失败，调用不得把加密包发布为成功。

恢复目标编排现在通过 `PortableInstanceCatalog` 在稳定 `data` 容器下创建唯一的新 `data/instances/restore-<随机ID>` 根并独占 lease。旧活动根仍为有效实例；活动选择记录位于 `data/launcher/active-instance.json`，只接受已验证目标的相对路径、实例 ID 和目标端口。固定恢复 helper 通过私有 stdio 接收口令、DSN 和目标路径，四次维护证明逐次核验目标 lease、owned PostgreSQL 身份和连接；凭据不进入 argv、环境或日志。

恢复顺序为新 PG17 初始化 → 固定 helper 解密并恢复数据库/文件 → 目标用户 DPAPI handoff 导入及读回校验 → Maintenance-only Python readiness/schema 审计 → smart stop → 生成预览。差异预览含来源/目标实例 ID、备份 SHA、逐表行数、恢复文件数、已撤销会话数和备份时点损失说明；现有 restore helper 尚未把备份创建时间或恢复文件总字节数返回给 Launcher，显示为未知，不伪造为零。PG 逻辑 dump 中的 SQL 可执行，因此开始恢复前要求用户明确确认信任备份来源。

恢复失败保留候选目录供诊断，不能原位重试，绝不改旧实例。活动指针提交必须再次证明候选服务停止，并持有旧实例 lease guard；旧 PG/Python 为 Running、Unknown、身份不匹配、路径不可读或缺少 lease/DPAPI 时均拒绝提交。`ConfirmActivationAsync` 的完整确认文字是唯一切换入口；切换后服务仍不自动启动，常规 Launcher 手动启动流程按新指针接续 Web/worker。没有恢复 UI，因此当前源码没有面向用户的恢复按钮。

上述实现目前仅有 fake 子进程/状态机与 DPAPI 隔离测试证据，不能代替真实 initdb/pg_restore/Host 编排恢复演练。当前恢复切片仍不得标记为 P1 完整恢复或发行放行。

## 加密包包含项

- `database.dump`：匹配 PG17 工具的 custom 逻辑备份，SQL 核验真实 PGDATA、实例数据库标记及 schema1。显式本机连接、密码不入 argv；同一导出快照统计各表行数。
- `files/`：state、assets、results 和 config/app.env；只读 inventory 报未知路径则拒绝，不能静默丢弃未识别文件。
- `recovery-keys.json`：业务加密 master key 和 secret key，只在加密容器内部，不放数据库连接密码、控制 token 或 DPAPI 密文。
- `backup.json`：版本、哈希、行数、文件清单、明确排除项与恢复要求。

不复制 PGDATA/cache/logs/本机进程身份。备份使用流式 AEAD 加密、口令 KDF、内部逐文件哈希和终结记录；容量限制用于坏包防护，低盘检查不自动删除任何文件。

## 恢复接入必须完成的工作

1. 用户选择可信备份并输入口令，先解密验证到全新私有目录，不覆盖旧实例。
2. 初始化全新受控PG集群和instance_id，生成新的数据库角色密码；单库dump不含集群角色。
3. 仅从已验证包恢复到空库，禁止 `--clean` 覆盖旧库；按schema1策略重建app/probe权限，不能把应用设为超级用户。
4. 将业务密钥通过目标Windows用户DPAPI重新保护，不直接搬旧DPAPI文件；恢复文件目录但不恢复旧进程身份、控制凭据或活动会话。
5. 禁止调度的维护模式检查身份/schema、逐表数量和关键字段、文件哈希与业务密钥解密。成功后展示差异与备份后数据损失风险，由用户确认切换，原实例完整保留。

PG恢复会执行备份中包含的SQL，不能因“口令正确”就信任陌生来源的备份。[pg_restore安全说明](https://www.postgresql.org/docs/17/app-pgrestore.html)

## 已有验证

`python -B -m tests.portable_schema_pg_smoke` 在唯一测试集群完成：真实逻辑导出、数据库+中文文件+恢复密钥同包加密、隔离解包、恢复到全新测试库、逐表行数/schema对账，以及恢复出的master key解密测试密文；随后正常停止并清理成功fixture。新增 Windows 恢复执行器尚未进入这条真实 PG 测试链。

这是同机器测试集群/新测试库的后端演练，不是跨机器完整迁移或恢复 UI 验收。目标集群重建、DPAPI 重新保护、会话撤销、维护审计和确认切换已有 Launcher 源码接线，但真实 Host→PG 恢复/故障演练及用户界面仍须完成，不能据此把 P1 备份恢复勾为完成。
