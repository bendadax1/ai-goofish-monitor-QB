"""Runtime path roots for source and future portable layouts.

This module deliberately has no filesystem-writing or environment-loading side
effects.  Existing consumers are migrated separately in small, reviewed batches.
"""

from __future__ import annotations

from dataclasses import dataclass
import logging
import os
from os import PathLike
from pathlib import Path, PureWindowsPath
from typing import Union


PathInput = Union[str, PathLike[str]]
logger = logging.getLogger(__name__)


def _normalize_root(value: PathInput, name: str) -> Path:
    root = Path(os.fspath(value))
    if not root.is_absolute():
        raise ValueError(f"{name} must be an absolute path: {value!s}")
    try:
        return root.resolve(strict=False)
    except OSError:
        logger.exception("Failed to normalize runtime path root", extra={"root_name": name})
        raise


def _validate_relative_part(value: PathInput) -> None:
    text = os.fspath(value)
    native_path = Path(text)
    windows_path = PureWindowsPath(text)
    if native_path.anchor or windows_path.anchor:
        raise ValueError(f"path part must be relative to its selected root: {text}")


@dataclass(frozen=True)
class RuntimePaths:
    """Explicit program, durable-data, and rebuildable-cache roots.

    Path parts are expected to be trusted application-relative names.  This is
    not a security sandbox: it does not resolve reparse points or police ``..``.
    """

    program_root: Path
    data_root: Path
    cache_root: Path

    def __post_init__(self) -> None:
        object.__setattr__(self, "program_root", _normalize_root(self.program_root, "program_root"))
        object.__setattr__(self, "data_root", _normalize_root(self.data_root, "data_root"))
        object.__setattr__(self, "cache_root", _normalize_root(self.cache_root, "cache_root"))

    @classmethod
    def create(
        cls,
        *,
        program_root: PathInput | None = None,
        data_root: PathInput | None = None,
        cache_root: PathInput | None = None,
    ) -> "RuntimePaths":
        """Build roots without consulting the process working directory.

        The default source-layout mapping anchors the program root at the
        repository directory and maps data/cache there too.  It preserves the
        new module's legacy-layout defaults only; existing consumers are not
        wired to this module yet.
        """

        selected_program_root: PathInput = (
            Path(__file__).resolve().parent.parent if program_root is None else program_root
        )
        selected_data_root = selected_program_root if data_root is None else data_root
        selected_cache_root = selected_program_root if cache_root is None else cache_root
        return cls(
            program_root=Path(selected_program_root),
            data_root=Path(selected_data_root),
            cache_root=Path(selected_cache_root),
        )

    def program_path(self, *parts: PathInput) -> Path:
        """Return a path joined to the selected program root."""

        return self._join(self.program_root, parts)

    def data_path(self, *parts: PathInput) -> Path:
        """Return a path joined to the selected durable-data root."""

        return self._join(self.data_root, parts)

    def cache_path(self, *parts: PathInput) -> Path:
        """Return a path joined to the selected rebuildable-cache root."""

        return self._join(self.cache_root, parts)

    @staticmethod
    def _join(root: Path, parts: tuple[PathInput, ...]) -> Path:
        for part in parts:
            _validate_relative_part(part)
        return root.joinpath(*(Path(os.fspath(part)) for part in parts))
