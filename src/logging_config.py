"""
统一日志配置模块

负责初始化和配置日志系统，提供统一的logger获取接口。

功能：
- 配置分等级日志（DEBUG/INFO/WARNING/ERROR/CRITICAL）
- 双输出通道（控制台+文件）
- JSON Lines格式存储
- 日志文件轮转
- 支持多进程任务日志
"""

import os
import logging
from logging.handlers import RotatingFileHandler
from pathlib import Path
from typing import Optional

from src.log_formatters import JSONLinesFormatter, ColoredConsoleFormatter, StructuredFilter
from src.log_retention import (
    _process_open_file_ids,
    has_reparse_ancestor,
    is_file_open,
    is_reparse_point,
)


# 全局配置
_logging_initialized = False
_loggers = {}


def setup_logging(
    log_dir: str = "logs",
    log_level: str = "INFO",
    console_level: str = "INFO",
    max_bytes: int = 10 * 1024 * 1024,  # 10MB
    backup_count: int = 10,
    retention_days: int = 7,
    enable_json: bool = True,
    enable_legacy: bool = True  # 兼容旧的fetcher.log
) -> None:
    """
    初始化日志系统
    
    Args:
        log_dir: 日志目录
        log_level: 文件日志等级
        console_level: 控制台日志等级
        max_bytes: 单个日志文件最大字节数
        backup_count: 保留的备份文件数量
        retention_days: 日志保留天数
        enable_json: 是否启用JSON格式
        enable_legacy: 是否同时输出到fetcher.log（向后兼容）
    """
    global _logging_initialized
    
    if _logging_initialized:
        return
    
    # 创建日志目录
    log_path = Path(log_dir)
    log_path.mkdir(exist_ok=True)
    (log_path / "tasks").mkdir(exist_ok=True)
    (log_path / "exports").mkdir(exist_ok=True)
    
    # 获取根logger
    root_logger = logging.getLogger()
    root_logger.setLevel(logging.DEBUG)  # 根logger设为最低级别
    
    # 清除现有handlers（避免重复）
    root_logger.handlers.clear()
    
    # 1. 控制台Handler - 彩色格式
    console_handler = logging.StreamHandler()
    console_handler.setLevel(getattr(logging, console_level.upper()))
    console_handler.setFormatter(ColoredConsoleFormatter())
    root_logger.addHandler(console_handler)
    
    # 2. 系统日志Handler - JSON格式
    if enable_json:
        system_log_path = log_path / "system.log"
        system_handler = RotatingFileHandler(
            system_log_path,
            maxBytes=max_bytes,
            backupCount=backup_count,
            encoding='utf-8'
        )
        system_handler.setLevel(getattr(logging, log_level.upper()))
        system_handler.setFormatter(JSONLinesFormatter())
        root_logger.addHandler(system_handler)
    
    # 3. 错误日志Handler - 仅ERROR及以上
    error_log_path = log_path / "error.log"
    error_handler = RotatingFileHandler(
        error_log_path,
        maxBytes=max_bytes,
        backupCount=backup_count,
        encoding='utf-8'
    )
    error_handler.setLevel(logging.ERROR)
    error_handler.setFormatter(JSONLinesFormatter() if enable_json else logging.Formatter(
        '[%(asctime)s] [%(name)s] [%(levelname)s] %(message)s'
    ))
    root_logger.addHandler(error_handler)
    
    # 4. 兼容旧的fetcher.log（可选）
    if enable_legacy:
        legacy_log_path = log_path / "fetcher.log"
        legacy_handler = RotatingFileHandler(
            legacy_log_path,
            maxBytes=max_bytes,
            backupCount=backup_count,
            encoding='utf-8'
        )
        legacy_handler.setLevel(getattr(logging, log_level.upper()))
        # 使用传统格式
        legacy_handler.setFormatter(logging.Formatter(
            '[%(asctime)s] [%(name)s] [%(levelname)s] %(message)s',
            datefmt='%Y-%m-%d %H:%M:%S'
        ))
        root_logger.addHandler(legacy_handler)
    
    _logging_initialized = True
    
    # 记录日志系统启动
    logger = get_logger("logging_config")
    logger.info(
        "日志系统初始化完成",
        extra={
            "service": "system",
            "event": "logging_initialized",
            "log_dir": log_dir,
            "log_level": log_level,
            "console_level": console_level
        }
    )


def get_logger(name: str, service: Optional[str] = None) -> logging.Logger:
    """
    获取logger实例
    
    Args:
        name: logger名称（通常使用__name__）
        service: 服务名称（web/collector/notifier等）
    
    Returns:
        配置好的logger实例
    """
    logger = logging.getLogger(name)
    
    # 添加结构化过滤器
    if service:
        structured_filter = StructuredFilter(service=service)
        logger.addFilter(structured_filter)
    
    return logger


def get_task_logger(task_name: str, task_id: int, log_dir: str = "logs") -> logging.Logger:
    """
    为特定任务创建独立的logger
    
    Args:
        task_name: 任务名称
        task_id: 任务ID
        log_dir: 日志目录
    
    Returns:
        任务专用logger
    """
    from src.portable.app_paths import get_portable_runtime_paths
    portable_paths = get_portable_runtime_paths()
    if portable_paths is not None and log_dir == "logs":
        log_dir = str(portable_paths.data_path("logs"))
    logger_name = f"task.{task_id}"
    
    # 如果已存在，直接返回
    if logger_name in _loggers:
        return _loggers[logger_name]
    
    # 创建新logger
    logger = logging.getLogger(logger_name)
    logger.setLevel(logging.DEBUG)
    
    # 不传播到根logger（避免重复记录）
    logger.propagate = True  # 改为True以便同时输出到控制台和系统日志
    
    # 添加任务专用文件handler
    log_path = Path(log_dir) / "tasks"
    log_path.mkdir(exist_ok=True)
    
    task_log_path = log_path / f"task_{task_id}.log"
    task_handler = RotatingFileHandler(
        task_log_path,
        maxBytes=10 * 1024 * 1024,  # 10MB
        backupCount=5,
        encoding='utf-8'
    )
    task_handler.setLevel(logging.DEBUG)
    task_handler.setFormatter(JSONLinesFormatter())
    
    # 添加结构化过滤器
    structured_filter = StructuredFilter(
        service="collector",
        task_id=task_id,
        task_name=task_name
    )
    task_handler.addFilter(structured_filter)
    logger.addFilter(structured_filter)
    
    logger.addHandler(task_handler)
    
    # 缓存logger
    _loggers[logger_name] = logger
    
    return logger


def cleanup_old_logs(log_dir: str = "logs", retention_days: int = 7) -> None:
    """
    清理过期日志文件
    
    Args:
        log_dir: 日志目录
        retention_days: 保留天数
    """
    import time
    
    log_path = Path(log_dir)
    try:
        log_path.lstat()
    except FileNotFoundError:
        return
    except OSError as e:
        get_logger(__name__).error(
            "检查日志目录失败，未执行清理",
            extra={"service": "system", "event": "log_cleanup_root_error"},
            exc_info=(type(e), e, e.__traceback__),
        )
        return
    if has_reparse_ancestor(log_path):
        return
    
    # Preserve the manual API's explicit retention_days semantics. The default
    # is seven days; setup_logging does not run this destructive cleanup.
    retention_days = int(retention_days)
    cutoff_time = time.time() - retention_days * 24 * 60 * 60
    active_paths = set()
    root_logger = logging.getLogger()
    handlers = list(root_logger.handlers)
    for configured_logger in logging.Logger.manager.loggerDict.values():
        if isinstance(configured_logger, logging.Logger):
            handlers.extend(configured_logger.handlers)
    for handler in handlers:
        active_path = getattr(handler, "baseFilename", None)
        if active_path:
            active_paths.add(active_path)
    open_file_ids = _process_open_file_ids()

    # Walk explicitly so junctions/symlinks are never traversed. The exports
    # subtree has its own retention policy and is not a source-log directory.
    def _walk_error(error: OSError) -> None:
        get_logger(__name__).error(
            "枚举日志目录失败，未能检查部分文件",
            extra={"service": "system", "event": "log_cleanup_enumerate_error"},
            exc_info=(type(error), error, error.__traceback__),
        )

    for current, directories, filenames in os.walk(
        log_path, followlinks=False, onerror=_walk_error
    ):
        current_path = Path(current)
        directories[:] = [
            name for name in directories
            if name.casefold() != "exports"
            and not is_reparse_point(current_path / name)
        ]
        for filename in filenames:
            log_file = current_path / filename
            rotated_suffix = filename.rsplit(".log.", 1)[-1]
            is_managed_log = filename.endswith(".log") or (
                ".log." in filename and rotated_suffix.isdecimal()
            )
            if not is_managed_log or is_reparse_point(log_file):
                continue
            try:
                file_stat = log_file.stat()
                if not log_file.is_file() or file_stat.st_mtime >= cutoff_time:
                    continue
                try:
                    file_is_open = is_file_open(log_file, active_paths, open_file_ids)
                except Exception as e:
                    logger = get_logger(__name__)
                    logger.error(
                        "无法核验日志文件是否仍在使用，已保留",
                        extra={"service": "system", "event": "log_cleanup_open_check_error"},
                        exc_info=(type(e), e, e.__traceback__),
                    )
                    continue
                if file_is_open:
                    continue
                log_file.unlink()
                logger = get_logger(__name__)
                logger.info(
                    f"已删除过期日志: {log_file.name}",
                    extra={"service": "system", "event": "log_cleanup"}
                )
            except OSError as e:
                logger = get_logger(__name__)
                logger.error(
                    f"删除日志文件失败: {log_file.name}",
                    extra={"service": "system", "event": "log_cleanup_error"},
                    exc_info=e
                )
