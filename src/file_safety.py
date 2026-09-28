"""跨平台单文件名与作用域文件边界校验，不加载业务配置。"""

import logging
from pathlib import Path, PureWindowsPath
import re
import stat


logger = logging.getLogger(__name__)


class UnsafeFilePathError(ValueError):
    """不携带真实路径的文件边界错误。"""


def validate_filename(filename: str) -> str:
    """拒绝路径/Windows 别名，不解码或改写用户文件名。"""
    text = str(filename or "").strip()
    stem = text.split(".", 1)[0].rstrip(" ").upper()
    reserved = {"CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$"}
    reserved.update(f"{prefix}{number}" for prefix in ("COM", "LPT") for number in "123456789¹²³")
    if (
        not text or text in {".", ".."} or ".." in text
        or any(character in text for character in '/\\:<>"|?*')
        or any(ord(character) < 32 for character in text)
        or PureWindowsPath(text).anchor
        or text.endswith(".") or stem in reserved
        # 拒绝编码与多重编码路径控制字符；不禁止普通名称中的百分号。
        or re.search(r"%(?:25)*(?:2f|5c|3a|00|2e)", text, re.IGNORECASE)
    ):
        raise UnsafeFilePathError("无效的文件名。")
    return text


def check_scoped_path(path: Path, root: Path, *, directory: bool = False) -> Path:
    """检查 root 下每个既有组件，拒绝链接/reparse point 和错误文件类型。

    返回原路径以保留既有相对路径语义。不跟随链接，不创建文件；调用方
    仍须处理实际 I/O 失败。本检查不承诺抵抗本地进程并发替换目录。
    """
    try:
        absolute = path.absolute()
        anchor = root.absolute()
        parts = absolute.relative_to(anchor).parts
        if not parts or any(part in {".", ".."} for part in parts):
            raise UnsafeFilePathError("文件路径超出允许范围。")
        current = anchor
        for index, part in enumerate((None, *parts)):
            if part is not None:
                current = current / part
            try:
                info = current.lstat()
            except FileNotFoundError:
                continue
            if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT:
                raise UnsafeFilePathError("文件路径不允许包含链接。")
            expected_directory = index < len(parts) or directory
            if expected_directory and not stat.S_ISDIR(info.st_mode):
                raise UnsafeFilePathError("文件路径包含非目录组件。")
            if not expected_directory and not stat.S_ISREG(info.st_mode):
                raise UnsafeFilePathError("目标不是普通文件。")
        absolute.resolve(strict=False).relative_to(anchor.resolve(strict=False))
    except UnsafeFilePathError:
        logger.warning("已拒绝不安全的作用域文件路径")
        raise
    except (OSError, RuntimeError, ValueError):
        logger.warning("无法安全校验作用域文件路径")
        raise UnsafeFilePathError("无法安全访问该文件路径。") from None
    return path
