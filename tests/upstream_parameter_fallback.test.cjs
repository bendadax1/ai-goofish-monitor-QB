// 隔离执行实际渲染/表单逻辑；无 DOM 库、网络或真实配置。
const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const { test } = require('node:test');

const view = readFileSync(join(__dirname, '../static/js/modules/settings_view.js'), 'utf8');
const render = readFileSync(join(__dirname, '../static/js/modules/render.js'), 'utf8');
const flag = 'AI_PARAMETER_FALLBACK_ENABLED';
const reasoningFlag = 'AI_REASONING_FALLBACK_ENABLED';
function between(source, start, end) {
    const from = source.indexOf(start);
    const to = source.indexOf(end, from);
    assert.ok(from >= 0 && to > from, '生产代码标记改变，需重新核对测试范围');
    return source.slice(from, to);
}

test('existing AI form renders strict opt-in with scope explanation', () => {
    const context = vm.createContext({});
    vm.runInContext(between(render, 'function renderAISettings(', '\nfunction '), context);
    for (const value of [undefined, false, 'true', true]) {
        context.settings = { [flag]: value, [reasoningFlag]: value };
        const html = vm.runInContext('renderAISettings(settings)', context);
        const input = html.match(/<input[^>]+id="ai-parameter-fallback-enabled"[^>]*>/)[0];
        assert.equal(input.includes('checked'), value === true);
        const reasoningInput = html.match(/<input[^>]+id="ai-reasoning-fallback-enabled"[^>]*>/)[0];
        assert.equal(reasoningInput.includes('checked'), value === true);
        assert.ok(html.includes('完整且字段一致的 JSON'));
        assert.ok(html.includes('原重试次数内'));
        assert.ok(html.includes('不移除 tokens 上限'));
        assert.ok(html.includes('不适用于连接测试或标准生成'));
    }
});

test('portable form refresh replaces stale checkbox and handles missing old field', () => {
    const checkbox = { checked: true };
    const reasoningCheckbox = { checked: true };
    const context = vm.createContext({
        aiForm: { querySelector: selector => ({
            '#ai-parameter-fallback-enabled': checkbox,
            '#ai-reasoning-fallback-enabled': reasoningCheckbox,
        })[selector] || null },
    });
    vm.runInContext(between(view, 'const applyAiSettingsToForm =', 'const applyProxySettingsToForm ='), context);
    for (const value of [false, true, undefined, 'true']) {
        context.settings = { [flag]: value, [reasoningFlag]: value };
        vm.runInContext('applyAiSettingsToForm(settings)', context);
        assert.equal(checkbox.checked, value === true);
        assert.equal(reasoningCheckbox.checked, value === true);
    }
});

for (const portable of [false, true]) {
    for (const enabled of [false, true]) {
        test(`save ${portable ? 'portable' : 'legacy'} checkbox=${enabled} sends JSON boolean`, async () => {
            let submitted;
            let payload;
            let genericPayload;
            let healthCalls = 0;
            const saveButton = { disabled: false, textContent: '保存' };
            const entries = [['OPENAI_MODEL_NAME', 'fixture'], ['AI_MAX_TOKENS_LIMIT', '4321']];
            if (enabled) entries.push([flag, 'on'], [reasoningFlag, 'on']);
            const context = vm.createContext({
                aiForm: {
                    addEventListener: (event, handler) => { assert.equal(event, 'submit'); submitted = handler; },
                    querySelector: () => saveButton,
                },
                FormData: class {
                    entries() { return entries; }
                    get(key) { return entries.find(([name]) => name === key)?.[1] ?? null; }
                },
                aiGenericToggleKeys: new Set(['ENABLE_THINKING', 'ENABLE_RESPONSE_FORMAT', 'AI_VISION_ENABLED']),
                buildAiGenericToggleSettings: () => ({ AI_VISION_ENABLED: false }),
                isPortableRevision: () => portable,
                aiSettings: { config_revision: 7, config_id: 'cfg-a' },
                updateAISettings: async settings => { payload = settings; return { message: 'saved' }; },
                updateAiGenericToggleSettings: async settings => { genericPayload = settings; },
                checkAIHealth: async () => { healthCalls++; },
                refreshPortableSettings: async () => ({ aiSettings: {}, proxySettings: {} }),
                refreshSystemStatusPanel: async () => {},
                Notification: { success: () => {}, error: message => assert.fail(message) },
            });
            vm.runInContext(between(view, "aiForm.addEventListener('submit',", 'const testBtn ='), context);
            await submitted({ preventDefault() {} });
            assert.equal(payload[flag], enabled);
            assert.equal(payload[reasoningFlag], enabled);
            assert.equal(payload.AI_MAX_TOKENS_LIMIT, 4321);
            assert.equal(payload.config_revision, portable ? 7 : undefined);
            assert.equal(payload.config_id, portable ? 'cfg-a' : undefined);
            assert.ok(!genericPayload || (!(flag in genericPayload) && !(reasoningFlag in genericPayload)));
            assert.equal(healthCalls, portable ? 0 : 1); // 维持原保存流程，不新增探测。
            assert.equal(saveButton.disabled, false);
        });
    }
}

test('connection probe form does not forward analysis-only compatibility option', () => {
    const start = view.indexOf("testBtn.addEventListener('click',");
    const code = between(view.slice(start), 'const formData =', '// 测试时仅在填写时传递tokens上限');
    const context = vm.createContext({
        aiForm: {},
        FormData: class { entries() { return [[flag, 'on'], [reasoningFlag, 'on'], ['OPENAI_MODEL_NAME', 'fixture']]; } },
        buildAiGenericToggleSettings: () => ({}), aiGenericToggleKeys: new Set(),
    });
    vm.runInContext(code, context);
    assert.equal(vm.runInContext(`Object.hasOwn(settings, '${flag}')`, context), false);
    assert.equal(vm.runInContext(`Object.hasOwn(settings, '${reasoningFlag}')`, context), false);
    assert.equal(vm.runInContext('settings.OPENAI_MODEL_NAME', context), 'fixture');
});
