"""httpx_compat 的 pattern 解析单元回归。

两个 HTTPX 版本对 no_proxy CIDR 的 pattern 形式不同，这里是版本升级时
最先失效的点，单独用纯函数测试锁定，不依赖具体 HTTPX 版本行为。

- httpx 0.28.1: ``all://[2001:db8::/32]``  —— 方括号包住「网络/前缀」整体
- httpx2:       ``all://[2001:db8::]/32``   —— 方括号只包住网络，前缀在括号外
"""

import unittest
from ipaddress import IPv6Network

from src.httpx_compat import _parse_no_proxy_network


class ParseNoProxyNetworkTests(unittest.TestCase):
    def test_httpx_028_bracket_wraps_whole_cidr(self):
        self.assertEqual(
            _parse_no_proxy_network("all://[2001:db8::/32]"),
            IPv6Network("2001:db8::/32"),
        )
        self.assertEqual(
            _parse_no_proxy_network("all://[::1/128]"),
            IPv6Network("::1/128"),
        )

    def test_httpx2_bracket_wraps_network_only(self):
        self.assertEqual(
            _parse_no_proxy_network("all://[2001:db8::]/32"),
            IPv6Network("2001:db8::/32"),
        )
        self.assertEqual(
            _parse_no_proxy_network("all://[::1]/128"),
            IPv6Network("::1/128"),
        )
        self.assertEqual(
            _parse_no_proxy_network("all://[2001:db8:ffff:ffff:ffff:ffff:ffff:ffff]/128"),
            IPv6Network("2001:db8:ffff:ffff:ffff:ffff:ffff:ffff/128"),
        )

    def test_star_prefixed_wildcards_are_stripped(self):
        # 部分版本的 no_proxy 条目会带 "*" 前缀；CIDR 形式下网段本身不含方括号。
        self.assertEqual(
            _parse_no_proxy_network("all://*2001:db8::/32"),
            IPv6Network("2001:db8::/32"),
        )

    def test_non_cidr_entries_are_preserved(self):
        for pattern in (
            "all://localhost",
            "all://127.0.0.1",
            "all://example.test",
            "all://*example.test",
            "all://*.sub.test",
            "all://*port.test:8443",
            "http://",
            "all://",
        ):
            with self.subTest(pattern=pattern):
                self.assertIsNone(_parse_no_proxy_network(pattern))

    def test_ipv4_cidr_is_not_claimed(self):
        # 本混入只处理 IPv6；IPv4 CIDR 交回 HTTPX 自身行为。
        self.assertIsNone(_parse_no_proxy_network("all://127.0.0.0/8"))
        self.assertIsNone(_parse_no_proxy_network("all://[127.0.0.0]/8"))

    def test_invalid_cidr_is_not_silently_accepted(self):
        # 非法网段不得被当作有效网络吞掉，应返回 None 让 HTTPX 走原有校验。
        self.assertIsNone(_parse_no_proxy_network("all://[::1]/129"))
        self.assertIsNone(_parse_no_proxy_network("all://[not-a-network]/32"))

    def test_unbalanced_bracket_is_rejected(self):
        self.assertIsNone(_parse_no_proxy_network("all://[2001:db8::/32"))


if __name__ == "__main__":
    unittest.main()
