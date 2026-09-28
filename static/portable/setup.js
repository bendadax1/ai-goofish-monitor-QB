"use strict";
(() => {
  const byId = (id) => document.getElementById(id);
  const form = byId("setup-form"), summary = byId("summary"), status = byId("status"), submit = byId("submit");
  const fields = ["password", "confirm"];
  let pending = true;
  let submitting = false, authorizationGeneration = 0;
  const clearSecrets = () => fields.forEach((id) => { byId(id).value = ""; });
  window.addEventListener("pagehide", clearSecrets);
  function showError(message, invalid = []) {
    summary.textContent = message;
    for (const [id, text] of invalid) {
      byId(id).setAttribute("aria-invalid", "true");
      byId(id + "-error").textContent = text;
      byId(id + "-error").hidden = false;
    }
    summary.hidden = false;
    summary.focus();
  }
  async function request(url, options) {
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), 35000);
    try {
      return await fetch(url, {credentials: "same-origin", redirect: "error", ...options, signal: controller.signal});
    } finally { clearTimeout(timeout); }
  }
  async function authorize() {
    if (submitting) return;
    const generation = ++authorizationGeneration;
    pending = true;
    submit.disabled = true;
    summary.hidden = true;
    fields.forEach((id) => { byId(id).disabled = true; });
    // Short-lived, single-use ticket only. Never persist it or the setup token.
    let ticket = new URLSearchParams(location.hash.slice(1)).get("ticket");
    history.replaceState(null, "", location.pathname);
    try {
      const response = ticket
        ? await request("/setup/authorize", {method: "POST", headers: {"Content-Type": "application/json"}, body: JSON.stringify({ticket})})
        : await request("/setup/session", {method: "GET"});
      ticket = null;
      if (generation !== authorizationGeneration) return;
      if (!response.ok) throw new Error("authorization unavailable");
      pending = false;
      fields.forEach((id) => { byId(id).disabled = false; });
      submit.disabled = false;
      status.textContent = "仅首次设置需要，以后使用 admin 和此密码登录。";
      byId("password").focus();
    } catch (_) {
      ticket = null;
      if (generation !== authorizationGeneration) return;
      console.error("首次设置授权未完成，未记录授权凭据。");
      status.textContent = "";
      showError("请回到启动器点击「打开管理页」，即可继续设置，无需复制任何设置码。");
    }
  }
  form.addEventListener("submit", async (event) => {
    event.preventDefault();
    if (pending) return;
    summary.hidden = true;
    fields.forEach((id) => { byId(id).removeAttribute("aria-invalid"); byId(id + "-error").hidden = true; });
    let password = byId("password").value;
    const invalid = [], passwordError = window.GoofishAccountPolicy.passwordError(password);
    if (passwordError) invalid.push(["password", passwordError]);
    if (password !== byId("confirm").value) invalid.push(["confirm", "两次输入的密码不一致。"]);
    if (invalid.length) { password = ""; showError("请检查密码后再继续。", invalid); return; }
    pending = true;
    submitting = true;
    submit.disabled = true;
    submit.textContent = "正在设置…";
    status.textContent = "正在提交给本机服务，请稍候。";
    let created = false;
    try {
      const response = await request("/setup", {method: "POST", headers: {"Content-Type": "application/json"}, body: JSON.stringify({password})});
      clearSecrets();
      if (response.status === 201) {
        created = true;
        submit.textContent = "正在进入…";
        // Use existing login so cookie/session/scheduler rules stay unchanged.
        const body = new FormData();
        body.append("username", "admin"); body.append("password", password);
        password = "";
        const login = await request("/login", {method: "POST", body, redirect: "follow"});
        if (!login.ok || !login.redirected || new URL(login.url).origin !== location.origin || new URL(login.url).pathname === "/login") throw new Error("login incomplete");
        location.replace("/");
      } else if (response.status === 409) {
        location.replace("/login");
      } else {
        const messages = {401: "授权已过期，请从启动器重新打开管理页。", 403: "页面来源校验失败，请从启动器重新打开管理页。", 422: "密码未通过检查，请按提示重新设置。", 429: "尝试次数较多，请等待 60 秒后重试。"};
        showError(messages[response.status] || "结果暂时无法确认，请刷新页面检查状态，不要重复提交。");
        pending = ![422, 429].includes(response.status);
      }
    } catch (_) {
      console.error("首次设置或自动登录未能确认完成，未自动重试。");
      showError(created ? "密码已设置成功，自动登录未完成。请使用 admin 和刚设置的密码登录。" : "连接中断或等待超时，结果未知。请刷新页面确认状态，不要重复提交。");
      if (created) byId("login-link").hidden = false;
    } finally {
      submitting = false;
      password = ""; clearSecrets(); status.textContent = "";
      if (!pending) { submit.disabled = false; submit.textContent = "设置密码并进入"; }
    }
  });
  window.addEventListener("hashchange", () => {
    if (new URLSearchParams(location.hash.slice(1)).has("ticket")) void authorize();
  });
  void authorize();
})();
