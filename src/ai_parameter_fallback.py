"""保守判别可省略的请求参数；不解析错误文本、不执行重试或修改配置。"""

from collections.abc import Mapping

from openai import BadRequestError


def unsupported_optional_parameter(error, sent_params, *, protected_parameter=""):
    """只信任明确的结构化 400 错误，未知网关格式保持原失败行为。"""
    if not isinstance(error, BadRequestError) or error.status_code != 400:
        return None
    body = error.body
    if not isinstance(body, Mapping):
        return None
    # SDK 通常已剥离 error 外层；兼容未剥离的同形结构。
    if "error" in body:
        if "code" in body or "param" in body:
            return None
        body = body["error"]
    if not isinstance(body, Mapping) or body.get("code") != "unsupported_parameter":
        return None
    parameter = body.get("param")
    if not isinstance(parameter, str) or parameter not in {"temperature", "response_format"}:
        return None
    if parameter == protected_parameter or parameter not in sent_params:
        return None
    return parameter
