"""纯响应夹具；不导入业务配置，不发网络请求。"""

import unittest
from types import SimpleNamespace

from src.ai_response import AIResponseContentError, extract_analysis_candidate, extract_final_text


def completion(content, *, finish_reason="stop", **message_fields):
    return SimpleNamespace(choices=[SimpleNamespace(
        finish_reason=finish_reason,
        message=SimpleNamespace(content=content, **message_fields),
    )])


class FinalTextTests(unittest.TestCase):
    def test_sdk_response_preserves_text(self):
        text = '  {"原因":"正常答案"}\n'
        self.assertEqual(extract_final_text(completion(text)), text)

    def test_dictionary_response(self):
        self.assertEqual(extract_final_text({"choices": [{"message": {"content": "答案"}}]}), "答案")

    def test_legacy_string_response(self):
        self.assertEqual(extract_final_text("```json\n{}\n```"), "```json\n{}\n```")

    def test_content_parts_preserve_json_segments(self):
        parts = [{"type": "text", "text": '{"原因":'}, SimpleNamespace(type="text", text='"正常"}')]
        self.assertEqual(extract_final_text(completion(parts)), '{"原因":"正常"}')

    def test_missing_choices_or_message(self):
        for value in (None, {}, {"choices": None}, {"choices": []}, {"choices": {}}, {"choices": [None]}):
            with self.subTest(value=value), self.assertRaises(AIResponseContentError):
                extract_final_text(value)

    def test_empty_content(self):
        for content in (None, "", " \r\n", [], [{"type": "text", "text": " "}]):
            with self.subTest(content=content), self.assertRaises(AIResponseContentError):
                extract_final_text(completion(content))

    def test_does_not_fallback_to_reasoning(self):
        with self.assertRaises(AIResponseContentError):
            extract_final_text(completion(None, reasoning_content='{"is_recommended":true}'))

    def test_explicit_analysis_candidate_and_final_priority(self):
        fallback = completion(None, reasoning_content='{"prompt_version":"test"}')
        with self.assertRaises(AIResponseContentError):
            extract_analysis_candidate(fallback)
        self.assertEqual(
            extract_analysis_candidate(fallback, allow_reasoning_content=True),
            ('{"prompt_version":"test"}', True),
        )
        self.assertEqual(
            extract_analysis_candidate(completion("最终答案", reasoning_content="不应读取"), allow_reasoning_content=True),
            ("最终答案", False),
        )

    def test_reasoning_candidate_never_bypasses_nonfinal_or_invalid_content(self):
        for response in (
            completion(None, finish_reason="length", reasoning_content="候选"),
            completion(None, refusal="拒绝", reasoning_content="候选"),
            completion(None, tool_calls=[{"id": "x"}], reasoning_content="候选"),
            completion({"type": "image"}, reasoning_content="候选"),
            completion(" ", reasoning_content=None),
        ):
            with self.subTest(response=response), self.assertRaises(AIResponseContentError):
                extract_analysis_candidate(response, allow_reasoning_content=True)

    def test_content_wins_over_reasoning(self):
        self.assertEqual(extract_final_text(completion("最终答案", reasoning_content="不应读取")), "最终答案")

    def test_rejects_explicit_nonfinal_finish_reasons(self):
        for reason in ("length", "content_filter", "tool_calls", "function_call", "unknown"):
            with self.subTest(reason=reason), self.assertRaises(AIResponseContentError):
                extract_final_text(completion('{"is_recommended":true}', finish_reason=reason))

    def test_missing_finish_reason_is_compatible(self):
        self.assertEqual(extract_final_text(completion("网关答案", finish_reason=None)), "网关答案")

    def test_refusal_or_tool_call_not_used_as_answer(self):
        for fields in ({"refusal": "拒绝细节"}, {"tool_calls": [{"id": "x"}]}, {"function_call": {"name": "x"}}):
            with self.subTest(fields=fields), self.assertRaises(AIResponseContentError):
                extract_final_text(completion("残留文本", **fields))

    def test_unknown_or_mixed_parts_are_not_silently_dropped(self):
        for content in (12, {}, ["text"], [{"type": "image_url", "text": "伪装文本"}],
                        [{"type": "text", "text": "有效"}, {"type": "refusal", "refusal": "拒绝"}]):
            with self.subTest(content=content), self.assertRaises(AIResponseContentError):
                extract_final_text(completion(content))

    def test_error_does_not_include_private_output(self):
        secret = "private-provider-output"
        try:
            extract_final_text(completion(secret, finish_reason="length", reasoning_content=secret))
        except AIResponseContentError as error:
            self.assertNotIn(secret, str(error))
        else:
            self.fail("应拒绝截断响应")

    def test_does_not_parse_or_rewrite_json(self):
        # JSON/schema 判断仍属于业务层，提取器不能制造推荐或挑选有利答案。
        text = '{} {"is_recommended":true}'
        self.assertEqual(extract_final_text(completion(text)), text)


if __name__ == "__main__":
    unittest.main()
