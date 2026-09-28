"""无配置、无 I/O 的 Chat Completions 最终文本提取。

默认只读取最终 content。分析调用可显式读取兼容网关的 reasoning_content，
完整 JSON/schema 校验仍由业务调用方负责；本模块不发起请求或重试。
"""

from collections.abc import Mapping
from typing import Any


class AIResponseContentError(ValueError):
    """响应未包含可用的最终文本；消息不携带模型原始输出。"""


def _field(value: Any, key: str, default=None):
    return value.get(key, default) if isinstance(value, Mapping) else getattr(value, key, default)


def _text_content(value: Any) -> str:
    if isinstance(value, str):
        return value
    if isinstance(value, (list, tuple)):
        parts = []
        for part in value:
            # 第三方网关可能将最终文本分成 text parts；拒绝/工具/图片不算文本。
            if _field(part, "type") == "text" and isinstance(_field(part, "text"), str):
                parts.append(_field(part, "text"))
            else:
                raise AIResponseContentError("AI响应包含不支持的内容片段，请检查模型配置。")
        return "".join(parts)
    if value is None:
        return ""
    raise AIResponseContentError("AI响应内容不是文本，请检查模型配置。")


def _final_message(response: Any) -> Any:
    choices = _field(response, "choices")
    if not isinstance(choices, (list, tuple)) or not choices:
        raise AIResponseContentError("AI响应缺少有效 choices，请检查模型配置。")
    choice = choices[0]
    finish_reason = _field(choice, "finish_reason")
    if finish_reason not in (None, "stop"):
        raise AIResponseContentError("AI响应未正常完成，可能被截断、过滤或要求工具调用。")
    message = _field(choice, "message")
    if message is None:
        raise AIResponseContentError("AI响应缺少 message，请检查模型配置。")
    if _field(message, "refusal") or _field(message, "tool_calls") or _field(message, "function_call"):
        raise AIResponseContentError("AI未返回最终答案，请检查需求或模型配置。")
    return message


def extract_final_text(response: Any) -> str:
    """读取 SDK 对象、同结构字典或历史裸字符串中的最终 content。"""
    content = response if isinstance(response, str) else _text_content(_field(_final_message(response), "content"))
    if not content.strip():
        raise AIResponseContentError("AI返回的最终内容为空，请检查模型配置或重试。")
    return content


def extract_analysis_candidate(response: Any, *, allow_reasoning_content: bool = False) -> tuple[str, bool]:
    """最终答案优先；显式启用时仅对空 content 读取兼容字段。"""
    if isinstance(response, str):
        return extract_final_text(response), False
    message = _final_message(response)
    content = _text_content(_field(message, "content"))
    if content.strip():
        return content, False
    if not allow_reasoning_content:
        raise AIResponseContentError("AI返回的最终内容为空，请检查模型配置或重试。")
    candidate = _text_content(_field(message, "reasoning_content"))
    if not candidate.strip():
        raise AIResponseContentError("AI响应缺少可用的兼容内容。")
    return candidate, True
