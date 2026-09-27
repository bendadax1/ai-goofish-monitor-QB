// Production refresh logic with deterministic interleaved Web/Launcher snapshots.
const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const { test } = require('node:test');

const source = readFileSync(join(__dirname, '../static/js/modules/settings_view.js'), 'utf8');
const start = source.indexOf('const refreshPortableSettings = async');
const end = source.indexOf('const handlePortableSettingsConflict', start);
assert.ok(start >= 0 && end > start);
const refreshCode = source.slice(start, end);

for (const section of ['ai', 'proxy']) {
    test(`refresh ${section} cannot advance the other form baseline`, async () => {
        const applied = [];
        const context = vm.createContext({
            fetchAISettings: async () => ({ config_revision: 9, model: 'launcher-new' }),
            fetchProxySettings: async () => ({ config_revision: 9, proxy: 'launcher-new' }),
            applyAiSettingsToForm: value => applied.push(['ai', value]),
            applyProxySettingsToForm: value => applied.push(['proxy', value]),
        });
        vm.runInContext(`let aiSettings = {config_revision: 7}; let proxySettings = {config_revision: 7}; ${refreshCode}`, context);
        await vm.runInContext(`refreshPortableSettings(['${section}'])`, context);
        assert.equal(vm.runInContext('aiSettings.config_revision', context), section === 'ai' ? 9 : 7);
        assert.equal(vm.runInContext('proxySettings.config_revision', context), section === 'proxy' ? 9 : 7);
        assert.equal(applied.length, 1);
        assert.equal(applied[0][0], section);
        // Conflict resolution explicitly replaces both forms and their baselines.
        await vm.runInContext("refreshPortableSettings(['ai', 'proxy'])", context);
        assert.equal(vm.runInContext('aiSettings.config_revision', context), 9);
        assert.equal(vm.runInContext('proxySettings.config_revision', context), 9);
    });
}
