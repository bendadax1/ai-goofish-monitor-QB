"""统一账号规则、API 模型及浏览器校验的一致性回归。"""

import json
from pathlib import Path
import subprocess
import unittest

from pydantic import ValidationError

from src.account_policy import browser_account_policy, validate_new_password, validate_username
from src.web.account_models import PasswordChange, UserCreate, UserPasswordReset


class AccountPolicyTests(unittest.TestCase):
    VALID = ("12345678", "abcdefgh", "密" * 8, "🔑" * 8, "x" * 72, "密" * 24, " " * 8)
    INVALID = ("", "1234567", "密" * 7, "🔑" * 7, "x" * 73, "密" * 25, "x" * 8 + chr(0xD800))

    def test_all_password_write_models_use_one_policy(self):
        for password in self.VALID:
            with self.subTest(length=len(password)):
                self.assertEqual(validate_new_password(password), password)
                self.assertEqual(UserCreate(username="admin", password=password).password, password)
                self.assertEqual(PasswordChange(old_password="old", new_password=password).old_password, "old")
                self.assertEqual(UserPasswordReset(new_password=password).new_password, password)
        for password in self.INVALID:
            with self.subTest(length=len(password)):
                with self.assertRaises(ValueError):
                    validate_new_password(password)
                for model, args in (
                    (UserCreate, {"username": "admin", "password": password}),
                    (PasswordChange, {"old_password": "old", "new_password": password}),
                    (UserPasswordReset, {"new_password": password}),
                ):
                    with self.assertRaises(ValidationError):
                        model(**args)

    def test_username_policy_does_not_silently_normalize(self):
        for username in ("admin", "用户名", "a" * 50):
            self.assertEqual(validate_username(username), username)
            self.assertEqual(UserCreate(username=username, password="12345678").username, username)
        for username in ("ab", "a" * 51, " admin", "admin ", "ad\nmin", "ad\x7fmin"):
            with self.assertRaises(ValueError):
                validate_username(username)
            with self.assertRaises(ValidationError):
                UserCreate(username=username, password="12345678")

    def test_browser_and_backend_agree(self):
        root = Path(__file__).resolve().parents[1]
        cases = []
        for password in (*self.VALID, *self.INVALID):
            try:
                validate_new_password(password)
                error = ""
            except ValueError as exc:
                error = str(exc)
            cases.append({"password": password, "error": error})
        script = r"""
const fs = require('node:fs'), vm = require('node:vm'), assert = require('node:assert/strict');
const input = JSON.parse(fs.readFileSync(0, 'utf8'));
global.window = {};
global.document = {documentElement: {dataset: {accountPolicy: JSON.stringify(input.policy)}}};
vm.runInThisContext(fs.readFileSync('static/js/account_policy.js', 'utf8'));
for (const item of input.cases) assert.equal(window.GoofishAccountPolicy.passwordError(item.password), item.error);
assert.equal(window.GoofishAccountPolicy.validationMessage([{msg: 'invalid', input: 'SECRET'}], 'fallback'), 'invalid');
assert.equal(window.GoofishAccountPolicy.validationMessage(null, 'fallback'), 'fallback');
global.GoofishAccountPolicy = window.GoofishAccountPolicy;
vm.runInThisContext(fs.readFileSync('static/js/modules/users_view.js', 'utf8'));
async function checkProfileHandler(bind, ids) {
    let handler, sent = [], notices = [];
    global.Notification = {warning: m => notices.push(m), error: m => notices.push(m), success: () => {}};
    const fields = Object.fromEntries(ids.map(id => [id, {value: ''}]));
    fields[ids[3]].addEventListener = (event, fn) => { handler = fn; };
    bind({querySelector: id => fields[id] || null}, {});
    assert.equal(typeof handler, 'function');
    global.fetch = async (url, options) => {
        sent.push({url, data: JSON.parse(options.body)});
        return {ok: true, json: async () => ({message: 'ok'})};
    };
    fields[ids[0]].value = 'old';
    fields[ids[1]].value = fields[ids[2]].value = '1234567';
    await handler();
    assert.equal(sent.length, 0);
    assert.match(notices.pop(), /8/);
    fields[ids[1]].value = fields[ids[2]].value = '12345678';
    await handler();
    assert.deepEqual(sent, [{url: '/api/users/me/password', data: {old_password: 'old', new_password: '12345678'}}]);
    assert.equal(fields[ids[0]].value + fields[ids[1]].value + fields[ids[2]].value, '');
    fields[ids[0]].value = 'old';
    fields[ids[1]].value = fields[ids[2]].value = '12345678';
    global.fetch = async () => { throw new Error('offline'); };
    await handler(); // Must handle rejected I/O without an unhandled rejection.
    assert.equal(notices.pop(), 'offline');
}
(async () => {
    await checkProfileHandler(attachProfileEventListeners,
        ['#old-password-input', '#new-password-input', '#confirm-password-input', '#change-password-btn']);
    await checkProfileHandler(attachProfilePageEventListeners,
        ['#profile-current-password', '#profile-new-password', '#profile-confirm-password', '#profile-change-password-btn']);
})().catch(error => { console.error(error); process.exitCode = 1; });
"""
        result = subprocess.run(["node", "-e", script], input=json.dumps({"policy": browser_account_policy(), "cases": cases}),
                                text=True, encoding="utf-8", capture_output=True, cwd=root, timeout=30)
        self.assertEqual(result.returncode, 0, result.stderr)


if __name__ == "__main__":
    unittest.main()
