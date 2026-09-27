# 便携组合包基础契约

> 状态：P1 preview-integration 构建基础；不是公开发行包或完整业务闭环。

`scripts/portable/build-portable-bundle.py` 只使用标准库。它把已验证的
Launcher 发布目录、Python、Chromium、PostgreSQL 组件和严格白名单的程序文件
组装到新的 staging 目录；验证成功后才在同一磁盘上原子移动到
`launcher/dist/portable-<release-id>/`。它拒绝覆盖已有输出，拒绝符号链接、
junction、路径穿越、敏感配置及未知运行时文件。`--dry-run` 会验证组件身份，
列出文件和估算峰值，但不会创建最终包或复制组件。

## 布局和 current.json

布局定义在 `scripts/portable/portable-bundle-layout.json`。输出根保留
`AiGoofish.Launcher.App.exe` 及其同级 .NET 文件；不能只改名 EXE，因为
runtimeconfig、deps 和 native 文件名仍须匹配。其他组件固定在：

```text
<bundle>/
  AiGoofish.Launcher.App.exe
  app/app-<version>/
  runtime/<python-runtime-id>/python.exe
  browsers/<browser-runtime-id>/chrome-win64/chrome.exe
  postgres/<postgres-runtime-id>/
  current.json
  bundle-manifest.json
```

`data/` 是首次运行才创建的用户数据根，不作为构建产物写入。`current.json`
格式版本为 1，包含 `release_id`、`platform`，以及 `app`、`runtime`、`browser`、
`postgres`、`launcher` 的 exact ID 与相对目录；app 还包含版本及 schema 范围。
当前 Launcher 已接入真实受管组件；但原生 Windows、干净系统及完整放行矩阵未全部验收，
因此 `release_status` 仍为 `preview-integration`，不得把该包称为正式发行。

## 程序内容和许可边界

程序只复制明确白名单：根入口 `collector.py`、`login.py`、`portable_*.py` 和
`web_server.py`，`src/**/*.py`、`static/`（排除 `static/avatars/`）、`templates/`、
`images/logo/` 与 `images/login-bg.png`。默认提示只从 Git HEAD blob 导出
`prompts/base_prompt.txt` 和 `prompts/bayes/bayes_v1.json`，不用工作树的可能
私有修改；指南固定为 `prompts/guide/bayes_guide.md` 与
`prompts/guide/weight_framework_guide.md`。

构建和扫描核对第三方通知 inventory；尚有通知缺口时拒绝放行。
扫描通过不等于已完成发行签名或来源认证。具体组件和包的验收记录见
[验收状态](PORTABLE_ACCEPTANCE_STATUS.md)。

## 使用

```powershell
python -B scripts/portable/build-portable-bundle.py --release-id preview-integration-v1.0.4.5 --launcher-root $publishPath --launcher-inventory $publishInventory --dry-run
```

实际复制仅在组件和布局共同验收后执行：

```powershell
python -B scripts/portable/build-portable-bundle.py --release-id preview-integration-v1.0.4.5 --launcher-root $publishPath --launcher-inventory $publishInventory
```

以上 `$publishPath`、`$publishInventory` 须分别取本次官方 Launcher 构建输出的
`PUBLISH_PATH`、`PUBLISH_INVENTORY`，不能省略 inventory 或冒用其他版本的验收结果。

本工具不下载组件、不执行业务 Web、不读取 `.env`、不创建 data、不会启动抓取、AI、
通知或账号登录。特定包已有真实 schema/用户和备份恢复证据，不代表所有包或公开发行通过；
干净 Windows、原生体验及发布门槛以验收状态表为准。
