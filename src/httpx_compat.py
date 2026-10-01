"""实例级 NO_PROXY IPv6 CIDR 兼容，不加载业务配置或改写进程环境。

不同 HTTPX 版本对 IPv6 CIDR 的处理不一致：
- httpx 0.28.1 把 CIDR 当成域名/端口 pattern，构造时即失败；
- httpx2 能解析 CIDR，但只在部分地址上真正旁路（例如 `[::1]` 旁路，
  `[2001:db8::1]` 仍走代理），与项目期望不一致。

本模块仅提取这些网段并做实例级旁路，其他代理映射和 TLS/连接池生命周期
交回原实现。使用 `_get_proxy_map` / `_transport_for_url` 两个私有扩展点，
两个版本均保留该接口；升级 HTTPX 时必须执行 test_upstream_httpx_compat
的路由和 SDK 默认值回归。

两版本 pattern 形式不同，解析需同时兼容：
- httpx 0.28.1: ``all://[2001:db8::/32]``  —— 方括号包住「网络/前缀」整体
- httpx2:       ``all://[2001:db8::]/32``   —— 方括号只包住网络，前缀在括号外
"""

from ipaddress import ip_address, ip_network, AddressValueError
import asyncio
import importlib
import logging

logger = logging.getLogger(__name__)

_PATTERN_PREFIX = "all://"


def _resolve_httpx_module_name() -> str:
    """按 openai SDK 的实际依赖决定使用哪个 HTTPX 库。

    不能按名字顺序瞎猜：openai 2.x 依赖 `httpx`，openai 3.x 依赖 `httpx2`，
    两者可能同时存在于环境中（例如 playright 等间接引入 httpx）。
    若兼容层选错库，被混入的客户端类与 SDK 内部使用的客户端就不是同一库，
    patch 静默失效——这正是本模块要防的场景。

    优先读取 openai 的依赖元数据；元数据不可用时回退到「SDK 基类实际所属模块」。
    """
    candidate = None
    try:
        import importlib.metadata as metadata

        for requirement in metadata.requires("openai") or []:
            name = requirement.split(";", 1)[0].strip()
            package = name.split("[", 1)[0].split("==")[0].split(">=")[0].split("<")[0].split("~=")[0].strip()
            if package in ("httpx", "httpx2"):
                candidate = package
                break
    except Exception:
        candidate = None

    if candidate:
        try:
            return importlib.import_module(candidate).__name__
        except ModuleNotFoundError:
            pass

    # 回退：看 SDK 默认客户端实际继承自哪个库
    try:
        from openai import DefaultAsyncHttpxClient

        for klass in DefaultAsyncHttpxClient.__mro__:
            root = klass.__module__.split(".", 1)[0]
            if root in ("httpx", "httpx2"):
                return root
    except Exception:
        pass

    # 最后回退：按可用性取第一个
    for name in ("httpx", "httpx2"):
        try:
            return importlib.import_module(name).__name__
        except ModuleNotFoundError:
            continue

    raise ModuleNotFoundError(
        "未找到可用的 HTTPX 库：需要安装 httpx（openai 2.x）或 httpx2（openai 3.x）"
    )


def _import_httpx():
    """返回当前环境中可用的 HTTPX 库模块（与 openai SDK 一致）。"""
    return importlib.import_module(_resolve_httpx_module_name())


def _import_httpcore():
    """返回与当前 HTTPX 配套的 httpcore 模块（httpcore 或 httpcore2）。"""
    if httpx.__name__ == "httpx2":
        preferred = ("httpcore2", "httpcore")
    else:
        preferred = ("httpcore", "httpcore2")
    for name in preferred:
        try:
            return importlib.import_module(name)
        except ModuleNotFoundError:
            continue
    raise ModuleNotFoundError(
        "未找到可用的 httpcore 库：需要安装 httpcore（openai 2.x）或 httpcore2（openai 3.x）"
    )


httpx = _import_httpx()
httpcore = _import_httpcore()
HTTPX_MODULE_NAME = httpx.__name__


def _parse_no_proxy_network(pattern: str):
    """从 no_proxy pattern 中解析 IPv6 网段；非 CIDR 或非 IPv6 返回 None。

    只解析 CIDR 形式，域名、IP、通配条目原样保留交给 HTTPX 自身处理。
    """
    if not pattern.startswith(_PATTERN_PREFIX):
        return None
    candidate = pattern[len(_PATTERN_PREFIX):]

    # httpx2：方括号只包住网络部分，前缀在括号之后，例如 "[2001:db8::]/32"
    if candidate.startswith("["):
        close = candidate.find("]")
        if close == -1:
            return None
        candidate = candidate[1:close] + candidate[close + 1:]

    # 通配前缀（部分版本的 no_proxy 条目会带 "*"）
    if candidate.startswith("*"):
        candidate = candidate[1:]

    if "/" not in candidate:
        return None
    try:
        network = ip_network(candidate, strict=False)
    except ValueError:
        return None
    return network if network.version == 6 else None


class _IPv6NoProxyMixin:
    def _get_proxy_map(self, proxy, allow_env_proxies):
        mapping = super()._get_proxy_map(proxy, allow_env_proxies)
        networks = []
        for pattern, target in list(mapping.items()):
            # 仅处理显式旁路（值为 None）的条目；指定代理的映射保持原样。
            if target is not None:
                continue
            network = _parse_no_proxy_network(pattern)
            if network is None:
                continue
            networks.append(network)
            del mapping[pattern]
        self._no_proxy_ipv6_networks = tuple(networks)
        if networks:
            logger.debug("已启用实例级 IPv6 CIDR 代理旁路兼容")
        return mapping

    def _transport_for_url(self, url):
        networks = getattr(self, "_no_proxy_ipv6_networks", ())
        if networks:
            try:
                address = ip_address(url.host)
            except (ValueError, AddressValueError):
                address = None
            if address is not None and any(address in network for network in networks):
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
