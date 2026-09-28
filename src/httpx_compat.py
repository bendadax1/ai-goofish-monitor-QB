"""实例级 NO_PROXY IPv6 CIDR 兼容，不加载业务配置或改写进程环境。

HTTPX 0.28.1 把 IPv6 CIDR 当成域名/端口 pattern，构造时即失败。
仅提取这些网段，其他代理映射和 TLS/连接池生命周期交回原实现。
本模块使用 _get_proxy_map / _transport_for_url 两个私有扩展点；升级
HTTPX 时必须执行 test_upstream_httpx_compat 的路由和 SDK 默认值回归。
"""

from ipaddress import ip_address, ip_network
import asyncio
import logging

logger = logging.getLogger(__name__)


class _IPv6NoProxyMixin:
    def _get_proxy_map(self, proxy, allow_env_proxies):
        mapping = super()._get_proxy_map(proxy, allow_env_proxies)
        networks = []
        for pattern, target in list(mapping.items()):
            if target is not None or not pattern.startswith("all://"):
                continue
            candidate = pattern.removeprefix("all://")
            if candidate.startswith("[") and candidate.endswith("]"):
                candidate = candidate[1:-1]
            elif candidate.startswith("*"):
                candidate = candidate[1:]
            if "/" not in candidate:
                continue
            try:
                network = ip_network(candidate, strict=False)
            except ValueError:
                # 非合法网段不吞掉：保留 HTTPX 原有校验/失败行为。
                continue
            if network.version == 6:
                networks.append(network)
                del mapping[pattern]
        self._no_proxy_ipv6_networks = tuple(networks)
        if networks:
            logger.debug("已启用实例级 IPv6 CIDR 代理旁路兼容")
        return mapping

    def _transport_for_url(self, url):
        if self._no_proxy_ipv6_networks:
            try:
                address = ip_address(url.host)
            except ValueError:
                address = None
            if address is not None and any(address in network for network in self._no_proxy_ipv6_networks):
                return self._transport
        return super()._transport_for_url(url)


def create_sdk_http_client(*, asynchronous: bool = True, **kwargs):
    """保留已安装 SDK 的 limits/timeout/redirects 默认值。

    延迟导入 SDK，避免纯路径/模型测试因导入业务工具而依赖 SDK 初始化。
    返回客户端由调用点按原有生命周期关闭，构造错误由原有错误边界处理。
    """
    from openai import DefaultAsyncHttpxClient, DefaultHttpxClient

    if asynchronous:
        class SDKClient(_IPv6NoProxyMixin, DefaultAsyncHttpxClient):
            def __del__(self):
                # 与 SDK 原默认 wrapper 相同：显式关闭优先，活动事件循环兜底。
                try:
                    if not self.is_closed:
                        asyncio.get_running_loop().create_task(self.aclose())
                except Exception:
                    logger.debug("异步客户端销毁时无法安排兜底关闭")
    else:
        class SDKClient(_IPv6NoProxyMixin, DefaultHttpxClient):
            def __del__(self):
                try:
                    if not self.is_closed:
                        self.close()
                except Exception:
                    logger.debug("同步客户端销毁时无法完成兜底关闭")
    return SDKClient(**kwargs)
