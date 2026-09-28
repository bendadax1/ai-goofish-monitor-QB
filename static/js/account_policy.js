"use strict";
(() => {
    // 数值和提示均由服务端模板注入；所有设置密码入口使用同一个校验器。
    let policy;
    try {
        policy = JSON.parse(document.documentElement.dataset.accountPolicy);
    } catch (_) {
        console.error('账号规则加载失败，请刷新页面。');
        throw new Error('账号规则加载失败，请刷新页面。');
    }
    window.GoofishAccountPolicy = Object.freeze({
        passwordHint: policy.passwordHint,
        passwordError(value) {
            const characters = Array.from(value);
            if (characters.length < policy.passwordMinLength) return `密码${policy.passwordHint}`;
            // TextEncoder 会替换孤立代理项，显式拒绝以与 Python UTF-8 严格编码一致。
            if (characters.some(c => c.length === 1 && c.charCodeAt(0) >= 0xD800 && c.charCodeAt(0) <= 0xDFFF)) return '密码含有无效字符';
            if (new TextEncoder().encode(value).length > policy.passwordMaxBytes) return policy.passwordTooLong;
            return '';
        },
        validationMessage(detail, fallback) {
            // 不回显 Pydantic 的 input / ctx，其中可能含原始密码。
            if (typeof detail === 'string') return detail;
            if (Array.isArray(detail)) return detail.map(item => item.msg).filter(Boolean).join('；') || fallback;
            return fallback;
        }
    });
})();
