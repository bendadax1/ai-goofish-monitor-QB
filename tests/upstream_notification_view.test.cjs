// 执行通知页面的实际表单与请求函数，不连接浏览器或外部通知。
const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const { test } = require('node:test');

const source = readFileSync(join(__dirname, '../static/js/modules/notifications_view.js'), 'utf8');
function section(start, end) {
    const from = source.indexOf(start);
    const to = source.indexOf(end, from);
    assert.ok(from >= 0 && to > from, '生产代码标记改变，需复核测试范围');
    return source.slice(from, to);
}

test('私有表单明确提交空密钥和解除任务绑定', () => {
    const context = vm.createContext({});
    vm.runInContext(section('const NOTIFICATION_CHANNEL_META =', 'const NOTIFICATION_CHANNEL_ORDER ='), context);
    vm.runInContext(section('function parseNotificationCardPayload(', '\nfunction getNotificationCardInfo('), context);
    const fields = [
        { key: 'url', value: 'https://push.invalid' },
        { key: 'token', value: '' },
    ];
    const card = {
        getAttribute: key => ({ 'data-config-id': 'cfg-a', 'data-channel-type': 'gotify', 'data-bound-task': 'old-task' })[key],
        querySelector: selector => ({
            '.notify-config-name': { value: '测试配置' },
            '.notify-bound-task-select': { value: '' },
            '.notify-enabled': { checked: true },
            '.notify-on-recommend': { checked: true },
            '.notify-on-complete': { checked: false },
        })[selector],
        querySelectorAll: () => fields.map(field => ({
            getAttribute: () => field.key,
            value: field.value,
        })),
    };
    context.card = card;
    const payload = vm.runInContext('parseNotificationCardPayload(card).payload', context);
    assert.equal(payload.config.token, '');
    assert.equal(payload.config.bound_task, '');
    assert.equal(payload.notify_on_complete, false);
});

test('每次点击生成独立请求 ID，并传递当前通知目标', async () => {
    const requests = [];
    let serial = 0;
    const context = vm.createContext({
        globalThis: { crypto: { randomUUID: () => `request-${++serial}` } },
        fetch: async (url, options) => {
            requests.push({ url, body: JSON.parse(options.body) });
            return { ok: true, json: async () => ({ success: true }) };
        },
        Notification: { warning: message => assert.fail(message) },
    });
    vm.runInContext(section('function getNotificationCardInfo(', '\nfunction getNotificationErrorMessage('), context);
    vm.runInContext(section('async function sendNotificationConfigTest(', '\nfunction getCurrentNotificationTabKey('), context);
    context.card = { getAttribute: key => ({
        'data-channel-type': 'ntfy', 'data-config-id': 'cfg-a', 'data-bound-task': 'task-a',
    })[key] };
    await vm.runInContext('sendNotificationConfigTest(card, "product")', context);
    await vm.runInContext('sendNotificationConfigTest(card, "product")', context);
    assert.equal(requests.length, 2);
    assert.equal(requests[0].url, '/api/notifications/test-product');
    assert.deepEqual(requests.map(item => item.body.request_id), ['request-1', 'request-2']);
    assert.equal(requests[0].body.config_id, 'cfg-a');
    assert.equal(requests[0].body.bound_task, 'task-a');
});

test('服务器变更保存后，表单移除未被后端保留的旧 token', async () => {
    const tokenField = { value: 'synthetic-old' };
    const context = vm.createContext({
        parseNotificationCardPayload: () => ({ configId: 'cfg-a', payload: { config: { server_url: 'https://new.invalid', token: 'synthetic-old' } } }),
        updateMyNotificationConfig: async () => ({ config: { config: { server_url: 'https://new.invalid', topic: 'topic' } } }),
    });
    vm.runInContext(section('function syncNotificationCardAfterSave(', '\nasync function fetchNotificationUserProfile('), context);
    vm.runInContext(section('async function ensureConfigPersistedForTesting(', '\nasync function renderServerNotificationView('), context);
    context.card = {
        getAttribute: key => ({ 'data-channel-type': 'ntfy' })[key],
        querySelector: () => tokenField,
    };
    await vm.runInContext('ensureConfigPersistedForTesting(card)', context);
    assert.equal(tokenField.value, '');
});
