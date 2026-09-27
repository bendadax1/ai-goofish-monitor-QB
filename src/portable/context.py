"""Bounded configuration context for the normal portable application mode.

This module intentionally uses only the standard library and ``RuntimePaths``.
It does not load dotenv files, create directories, or import application code.
"""

from __future__ import annotations

import os
from pathlib import Path
from typing import Mapping

from src.runtime_paths import RuntimePaths


PORTABLE_MODE_ENVIRONMENT_VARIABLE = "GOOFISH_PORTABLE_MODE"
PORTABLE_PROGRAM_ROOT_ENVIRONMENT_VARIABLE = "GOOFISH_PORTABLE_PROGRAM_ROOT"
PORTABLE_DATA_ROOT_ENVIRONMENT_VARIABLE = "GOOFISH_PORTABLE_DATA_ROOT"
PORTABLE_CACHE_ROOT_ENVIRONMENT_VARIABLE = "GOOFISH_PORTABLE_CACHE_ROOT"
PORTABLE_DATABASE_URL_ENVIRONMENT_VARIABLE = "GOOFISH_PORTABLE_DATABASE_URL"

_PORTABLE_MODE_VALUES = {"1", "true", "yes", "on", "portable"}
_LEGACY_MODE_VALUES = {"0", "false", "no", "off", "legacy"}


class PortableConfigurationError(ValueError):
    """Raised when an explicitly requested portable configuration is unsafe."""


def portable_mode(*, environ: Mapping[str, str] | None = None) -> bool:
    """Return whether the process explicitly selected portable mode.

    An omitted setting preserves legacy behavior.  A supplied value must be an
    explicit supported value so a typo cannot silently select the legacy path.
    """

    environment = os.environ if environ is None else environ
    raw_value = environment.get(PORTABLE_MODE_ENVIRONMENT_VARIABLE)
    if raw_value is None:
        return False

    value = str(raw_value).strip().lower()
    if value in _PORTABLE_MODE_VALUES:
        return True
    if value in _LEGACY_MODE_VALUES:
        return False
    raise PortableConfigurationError(
        f"{PORTABLE_MODE_ENVIRONMENT_VARIABLE} must be one of portable/legacy boolean values"
    )


def portable_paths(*, environ: Mapping[str, str] | None = None) -> RuntimePaths:
    """Return the three explicitly configured portable roots.

    The program root must never double as a writable data/cache root.  No
    directories are created here; callers decide when their own I/O is needed.
    """

    environment = os.environ if environ is None else environ
    selected_roots: dict[str, str] = {}
    for variable in (
        PORTABLE_PROGRAM_ROOT_ENVIRONMENT_VARIABLE,
        PORTABLE_DATA_ROOT_ENVIRONMENT_VARIABLE,
        PORTABLE_CACHE_ROOT_ENVIRONMENT_VARIABLE,
    ):
        value = environment.get(variable, "")
        if not str(value).strip():
            raise PortableConfigurationError(f"{variable} is required in portable mode")
        selected_roots[variable] = str(value)

    try:
        paths = RuntimePaths.create(
            program_root=selected_roots[PORTABLE_PROGRAM_ROOT_ENVIRONMENT_VARIABLE],
            data_root=selected_roots[PORTABLE_DATA_ROOT_ENVIRONMENT_VARIABLE],
            cache_root=selected_roots[PORTABLE_CACHE_ROOT_ENVIRONMENT_VARIABLE],
        )
    except (OSError, ValueError) as exc:
        raise PortableConfigurationError("portable roots must be valid absolute paths") from exc

    def roots_overlap(first: Path, second: Path) -> bool:
        try:
            first.relative_to(second)
            return True
        except ValueError:
            try:
                second.relative_to(first)
                return True
            except ValueError:
                return False

    if roots_overlap(paths.program_root, paths.data_root) or roots_overlap(paths.program_root, paths.cache_root):
        raise PortableConfigurationError("portable program root must be separate from writable roots")
    return paths


def portable_app_env_path(*, environ: Mapping[str, str] | None = None) -> Path:
    """Return the single explicit portable preference file location."""

    return portable_paths(environ=environ).data_path("config", "app.env")


__all__ = [
    "PORTABLE_CACHE_ROOT_ENVIRONMENT_VARIABLE",
    "PORTABLE_DATA_ROOT_ENVIRONMENT_VARIABLE",
    "PORTABLE_DATABASE_URL_ENVIRONMENT_VARIABLE",
    "PORTABLE_MODE_ENVIRONMENT_VARIABLE",
    "PORTABLE_PROGRAM_ROOT_ENVIRONMENT_VARIABLE",
    "PortableConfigurationError",
    "portable_app_env_path",
    "portable_mode",
    "portable_paths",
]
