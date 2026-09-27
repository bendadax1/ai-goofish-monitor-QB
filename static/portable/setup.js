"use strict";
(() => {
  const byId = (id) => document.getElementById(id);
  const form = byId("setup-form"), summary = byId("summary"), status = byId("status");
  const fields = ["setup-token", "username", "password", "confirm"];
  const errors = ["token-error", "username-error", "password-error", "confirm-error"];
  const submit = byId("submit");
  let pending = false;
  const clearSecrets = () => ["setup-token", "password", "confirm"].forEach((id) => { byId(id).value = ""; });
  window.addEventListener("pagehide", clearSecrets);
  function showError(message, invalid = []) {
    summary.replaceChildren(document.createTextNode(message));
    if (invalid.length) {
      const list = document.createElement("ul");
      for (const [index, text] of invalid) {
        const item = document.createElement("li"), link = document.createElement("a");
        link.href = `#${fields[index]}`; link.textContent = text;
        item.append(link); list.append(item);
        byId(fields[index]).setAttribute("aria-invalid", "true");
        byId(errors[index]).textContent = text; byId(errors[index]).hidden = false;
      }
      summary.append(list);
    }
    summary.hidden = false; summary.focus();
  }
  form.addEventListener("submit", async (event) => {
    event.preventDefault();
    if (pending) return;
    summary.hidden = true; status.textContent = "";
    fields.forEach((id, index) => { byId(id).removeAttribute("aria-invalid"); byId(errors[index]).hidden = true; });
    const username = byId("username").value, password = byId("password").value;
    const setupToken = byId("setup-token").value, invalid = [];
    if (!/^[\x21-\x7e]{32,256}$/.test(setupToken)) invalid.push([0, "请粘贴 Launcher 提供的完整一次性设置码。"]);
    if (Array.from(username).length < 3 || Array.from(username).length > 50 || username !== username.trim() || /[\x00-\x1f\x7f]/.test(username)) invalid.push([1, "用户名需为 3–50 个字符，且不能含首尾空格或控制字符。"]);
    const bytes = new TextEncoder().encode(password).length;
    if (bytes < 12 || bytes > 72 || !/\p{Ll}/u.test(password) || !/\p{Lu}/u.test(password) || !/\p{Nd}/u.test(password) || !/[^\p{L}\p{N}]/u.test(password)) invalid.push([2, "请按上方要求设置密码（12–72 个 UTF-8 字节）。"]);
    if (password !== byId("confirm").value) invalid.push([3, "两次输入的密码不一致。"]);
    if (invalid.length) { showError("请检查以下内容：", invalid); return; }
    pending = true; submit.disabled = true; submit.textContent = "正在创建…";
    status.textContent = "正在提交给本机服务，请勿重复操作。";
    const controller = new AbortController(), timeout = setTimeout(() => controller.abort(), 35000);
    try {
      const response = await fetch("/setup", { method:"POST", headers:{"Content-Type":"application/json"}, credentials:"same-origin", redirect:"error", signal:controller.signal, body:JSON.stringify({username, password, setup_token:setupToken}) });
      clearSecrets();
      if (response.status === 201) {
        form.hidden = true; summary.hidden = true;
        byId("success").hidden = false; byId("success").focus();
      } else if (response.status === 409) {
        form.hidden = true; showError("首次设置已完成。刷新页面后进入登录，不要再次创建账户。");
      } else {
        const messages = {401:"设置码不正确，请返回 Launcher 核对。",403:"页面来源校验失败，请从 Launcher 重新打开本机页面。",422:"账户信息未通过检查，请核对用户名和密码要求。",429:"尝试次数较多，请等待 60 秒后重新输入。",503:"结果暂时无法确认，请刷新页面检查是否已经完成，不要重复提交。"};
        showError(messages[response.status] || "设置未完成，请检查 Launcher 状态后刷新页面。");
        pending = response.status >= 500;
      }
    } catch (_) {
      clearSecrets();
      console.error("首次设置请求未能确认完成，未自动重试。");
      showError("连接中断或等待超时，结果未知。请刷新页面确认状态，不要重复提交。");
    } finally {
      clearTimeout(timeout); status.textContent = "";
      if (!pending) { submit.disabled = false; submit.textContent = "创建管理员账户"; }
    }
  });
})();
