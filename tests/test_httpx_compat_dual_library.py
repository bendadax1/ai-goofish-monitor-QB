"""httpx_compat 的双库兼容回归（纯逻辑，不依赖具体库的测试基础设施）。

在 httpx 与 httpx2 两种环境下都应通过：
  1. 兼容导入层解析到可用库
  2. no_proxy pattern 解析（两版本的格式差异）
  3. 生产模块可导入
"""

import os
import sys
import unittest

sys.path.insert(0, os.getcwd())

import src.httpx_compat as compat
from src.httpx_compat import (
    HTTPX_MODULE_NAME,
    _parse_no_proxy_network,
    create_sdk_http_client,
    httpcore,
    httpx,
)
from ipaddress import IPv6Network


class CompatImportTests(unittest.TestCase):
    def test_resolves_one_of_two_libraries(self):
        self.assertIn(HTTPX_MODULE_NAME, ("httpx", "httpx2"))
        self.assertTrue(httpx.__version__)

    def test_httpcore_matches_httpx_family(self):
        expected = {"httpx": "httpcore", "httpx2": "httpcore2"}[HTTPX_MODULE_NAME]
        self.assertEqual(httpcore.__name__, expected)

    def test_production_modules_import_cleanly(self):
        import src.config          # noqa: F401
        import src.prompt_utils    # noqa: F401
        import src.web.ai_health   # noqa: F401


class PatternParseTests(unittest.TestCase):
    def test_httpx_028_format(self):
        self.assertEqual(
            _parse_no_proxy_network("all://[2001:db8::/32]"),
            IPv6Network("2001:db8::/32"),
        )

    def test_httpx2_format(self):
        self.assertEqual(
            _parse_no_proxy_network("all://[2001:db8::]/32"),
            IPv6Network("2001:db8::/32"),
        )
        self.assertEqual(
            _parse_no_proxy_network("all://[::1]/128"),
            IPv6Network("::1/128"),
        )

    def test_non_cidr_preserved(self):
        for p in ("all://localhost", "all://127.0.0.1", "all://example.test",
                  "all://*example.test", "all://*.sub.test", "http://", "all://"):
            with self.subTest(p=p):
                self.assertIsNone(_parse_no_proxy_network(p))

    def test_ipv4_not_claimed(self):
        self.assertIsNone(_parse_no_proxy_network("all://127.0.0.0/8"))

    def test_invalid_rejected(self):
        self.assertIsNone(_parse_no_proxy_network("all://[::1]/129"))
        self.assertIsNone(_parse_no_proxy_network("all://[bad]/32"))
        self.assertIsNone(_parse_no_proxy_network("all://[2001:db8::/32"))


class CidrCaptureTests(unittest.TestCase):
    def test_captures_ipv6_cidr_from_environment(self):
        import subprocess, textwrap
        env = {k: v for k, v in os.environ.items() if "proxy" not in k.lower()}
        env["PYTHONUTF8"] = "1"
        code = textwrap.dedent("""
            import os
            os.environ["HTTP_PROXY"] = "http://proxy.test:8080"
            os.environ["NO_PROXY"] = "::1/128,2001:db8::/32,localhost,127.0.0.1"
            import sys; sys.path.insert(0, os.getcwd())
            from src.httpx_compat import create_sdk_http_client
            c = create_sdk_http_client(asynchronous=False)
            print(len(c._no_proxy_ipv6_networks))
            c.close()
        """)
        r = subprocess.run([sys.executable, "-c", code], capture_output=True,
                           text=True, encoding="utf-8", errors="replace",
                           env=env, timeout=120)
        self.assertEqual(r.stdout.strip().splitlines()[-1], "2",
                         msg=f"stdout={r.stdout!r} stderr={r.stderr[-400:]!r}")


if __name__ == "__main__":
    unittest.main()
