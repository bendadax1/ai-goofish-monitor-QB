"""新建账号和设置密码的统一规则；不用于校验已有账号的登录凭据。

保持无配置、无存储依赖，便携首次设置和普通 Web 共用。
"""

import re

USERNAME_MIN_LENGTH = 3
USERNAME_MAX_LENGTH = 50
PASSWORD_MIN_LENGTH = 8
PASSWORD_MAX_BYTES = 72  # 现有 bcrypt 的 UTF-8 输入上限。
PASSWORD_HINT = f"至少 {PASSWORD_MIN_LENGTH} 位，允许纯数字"
PASSWORD_TOO_LONG = f"密码过长，UTF-8 编码后不能超过 {PASSWORD_MAX_BYTES} 字节"
USERNAME_HINT = f"用户名须为 {USERNAME_MIN_LENGTH}–{USERNAME_MAX_LENGTH} 个字符，不能含首尾空白或控制字符"
_USERNAME_CONTROL_PATTERN = re.compile(r"[\x00-\x1f\x7f]")


def validate_username(value: str) -> str:
    if (
        not isinstance(value, str)
        or not USERNAME_MIN_LENGTH <= len(value) <= USERNAME_MAX_LENGTH
        or value != value.strip()
        or _USERNAME_CONTROL_PATTERN.search(value)
    ):
        raise ValueError(USERNAME_HINT)
    return value


def validate_new_password(value: str) -> str:
    if not isinstance(value, str) or len(value) < PASSWORD_MIN_LENGTH:
        raise ValueError(f"密码{PASSWORD_HINT}")
    try:
        encoded = value.encode("utf-8")
    except UnicodeEncodeError:
        raise ValueError("密码含有无效字符") from None
    if len(encoded) > PASSWORD_MAX_BYTES:
        raise ValueError(PASSWORD_TOO_LONG)
    return value


def browser_account_policy() -> dict:
    """模板向浏览器传递规则，避免前端各表单维护独立常量。"""
    return {
        "passwordMinLength": PASSWORD_MIN_LENGTH,
        "passwordMaxBytes": PASSWORD_MAX_BYTES,
        "passwordHint": PASSWORD_HINT,
        "passwordTooLong": PASSWORD_TOO_LONG,
    }
