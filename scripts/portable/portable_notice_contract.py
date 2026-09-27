"""Shared fail-closed contract for portable release third-party notices."""

from __future__ import annotations

import re
from pathlib import Path
from typing import Any


REQUIRED_NON_WHEEL_COMPONENTS = {
    "dotnet-runtime-notices": ("10.0.12", "THIRD-PARTY-NOTICES.TXT", "6d15e10a101c6bfff2ab4429ed061bf76c456fc4b23ad6b03e0d0f8377148a21"),
    "avalonia-ui": ("12.1.2", "avalonia-MIT.txt", "213814d306090074d234d760239ff0f67eb9b8d20eefb4d5631bb39dbe0b769b"),
    "dotnet-runtime-license": ("10.0.12", "LICENSE.TXT", "d7a68596ab69b06f51ca278a6545148e4269a9381c26d597c13df5d88e08cf5b"),
    "avalonia-angle-windows": ("2.1.27548.20260419", "LICENSE", "54aff7276217df9f6b5181613999d208c9e40d2b1d51bf55217837e6871a4a63"),
    "harfbuzzsharp-win32": ("8.3.1.3", "THIRD-PARTY-NOTICES.txt", "21504c46c4c58aa64c1055bd2dcbc5f9a136b4b8c412ed3cc6740e22c5b127f5"),
    "skiasharp-win32": ("3.119.4", "THIRD-PARTY-NOTICES.txt", "21504c46c4c58aa64c1055bd2dcbc5f9a136b4b8c412ed3cc6740e22c5b127f5"),
    "python-embedded": ("3.13.15", "LICENSE.txt", "62bec384df47b0328307db41455ff6ea2559e5546b394ac69148561b21703120"),
    "playwright": ("1.57.0", "ThirdPartyNotices.txt", "3c338d6e7da464cd2c935351162dc472ca21d807cb81b9eccf521bb35731d8da"),
    "chromium": ("143 / r1200", "chromium-143-r1200-credits.html", "baac582df59212838afdd377fd757412e79d3865d7ff2e7c2b9f898e6513cd6b"),
    "chromium-onnxruntime-headers": ("v1.23.0", "onnxruntime-headers-v1.23.0-MIT.txt", "2f07c72751aed99790b8a4869cf2311df85a860b22ded05fa22803587a48922c"),
    "postgresql": ("17.11-3", "server_license.txt", "39294f3f5b41533b307e118ee37c356f32cbc7746fc97e8b91f1b49a9068a24e"),
}
EXPECTED_WHEEL_NOTICE_COUNT = 93
_LOCKED_REQUIREMENT = re.compile(r"^([A-Za-z0-9][A-Za-z0-9_.-]*)==([^\s\\]+)", re.MULTILINE)
_HASH = re.compile(r"^[0-9a-f]{64}$")


def canonicalize_distribution(value: str) -> str:
    return re.sub(r"[-_.]+", "-", value).lower()


def load_locked_distributions(lock_path: Path) -> dict[str, tuple[str, str]]:
    """Return canonical name -> (declared name, locked version) from pip-compile output."""
    try:
        lock_text = lock_path.read_text(encoding="utf-8")
    except (OSError, UnicodeError) as exc:
        raise ValueError("Python requirements lock is unreadable") from exc
    result: dict[str, tuple[str, str]] = {}
    for name, version in _LOCKED_REQUIREMENT.findall(lock_text):
        canonical = canonicalize_distribution(name)
        if canonical in result:
            raise ValueError("Python requirements lock contains duplicate distributions")
        result[canonical] = (name, version)
    if not result:
        raise ValueError("Python requirements lock contains no distributions")
    return result


def _wheel_component_id(distribution: str, cache_relative_path: str) -> str:
    try:
        notice_relative_path = cache_relative_path.split(".dist-info/", 1)[1]
    except IndexError:
        return ""
    if notice_relative_path.startswith("licenses/"):
        notice_relative_path = notice_relative_path.removeprefix("licenses/")
    slug = lambda value: re.sub(r"[^a-z0-9]+", "-", value.lower()).strip("-")
    return f"python-wheel-{slug(distribution)}-{slug(notice_relative_path)}"


def validate_notice_inventory(
    inventory: Any,
    lock_path: Path,
    *,
    packaged_notice_paths: set[str] | None = None,
    packaged_notice_hashes: dict[str, str] | None = None,
    allow_incomplete_preview: bool = False,
) -> None:
    """Validate inventory shape/completeness independently of its complete flag."""
    if not isinstance(inventory, dict) or inventory.get("format_version") != 1:
        raise ValueError("third-party notice inventory format is unsupported")
    components = inventory.get("components")
    if not isinstance(components, list):
        raise ValueError("third-party notice inventory components are malformed")
    if inventory.get("notice_complete") is not True and not allow_incomplete_preview:
        raise ValueError("third-party notice inventory is incomplete")

    by_id: dict[str, dict[str, Any]] = {}
    all_notice_paths: set[str] = set()
    for entry in components:
        if not isinstance(entry, dict) or not isinstance(entry.get("component"), str):
            raise ValueError("third-party notice component entry is malformed")
        component = entry["component"]
        if component in by_id:
            raise ValueError("third-party notice inventory contains duplicate components")
        by_id[component] = entry
        if entry.get("evidence_verified") is not True or (
            entry.get("is_gap") is not False and not (allow_incomplete_preview and entry.get("is_gap") is True)
        ):
            raise ValueError("third-party notice inventory contains unverified or gap entries")
        version = entry.get("version")
        if not isinstance(version, str) or not version:
            raise ValueError("third-party notice component version is missing")
        notice_path = entry.get("file", entry.get("bundle_target", entry.get("notice_source")))
        digest = entry.get("sha256", entry.get("evidence_sha256"))
        if not isinstance(notice_path, str) or not isinstance(digest, str) or not _HASH.fullmatch(digest):
            raise ValueError("third-party notice path or hash is malformed")
        if notice_path in all_notice_paths:
            raise ValueError("third-party notice inventory contains duplicate paths")
        all_notice_paths.add(notice_path)

    for component, (expected_version, filename, expected_hash) in REQUIRED_NON_WHEEL_COMPONENTS.items():
        entry = by_id.get(component)
        if entry is None or entry.get("version") != expected_version or component.startswith("python-wheel-"):
            raise ValueError(f"required locked notice component is missing or changed: {component}")
        expected_target = f"third-party-notices/{component}/{filename}"
        target = entry.get("file", entry.get("bundle_target"))
        digest = entry.get("sha256", entry.get("evidence_sha256"))
        if target != expected_target or digest != expected_hash:
            raise ValueError(f"required notice target or pinned hash changed: {component}")
        if entry.get("is_gap") is True and not allow_incomplete_preview:
            raise ValueError(f"required notice is unresolved: {component}")

    non_wheel_ids = {component for component in by_id if not component.startswith("python-wheel-")}
    if non_wheel_ids != set(REQUIRED_NON_WHEEL_COMPONENTS):
        raise ValueError("non-wheel notice inventory differs from the 11 locked components")

    wheels = [entry for entry in components if entry["component"].startswith("python-wheel-")]
    if len(wheels) != EXPECTED_WHEEL_NOTICE_COUNT:
        raise ValueError("Python wheel notice inventory does not contain the locked 93 notices")
    locked = load_locked_distributions(lock_path)
    if len(locked) != 60:
        raise ValueError("Python requirements lock no longer contains the reviewed 60 distributions")

    observed_distributions: set[str] = set()
    observed_targets: set[str] = set()
    observed_sources: set[str] = set()
    for entry in wheels:
        distribution = entry.get("distribution")
        cache_path = entry.get("cache_relative_path")
        target = entry.get("file", entry.get("bundle_target"))
        if not isinstance(distribution, str) or not isinstance(cache_path, str) or not isinstance(target, str):
            raise ValueError("Python wheel notice lacks distribution or source/target identity")
        canonical = canonicalize_distribution(distribution)
        locked_distribution = locked.get(canonical)
        if locked_distribution is None or entry["version"] != locked_distribution[1]:
            raise ValueError("Python wheel notice distribution/version differs from requirements lock")
        if entry["component"] != _wheel_component_id(distribution, cache_path):
            raise ValueError("Python wheel notice component id does not match its distribution/file")
        target_path = f"third-party-notices/{entry['component']}/{Path(cache_path).name}"
        if target != target_path:
            raise ValueError("Python wheel notice target does not match its component/file")
        if ".dist-info/" not in cache_path:
            raise ValueError("Python wheel notice cache path is not inside a dist-info directory")
        license_files = entry.get("metadata_license_files")
        if not isinstance(license_files, list) or any(not isinstance(value, str) or not value for value in license_files):
            raise ValueError("Python wheel notice license-file metadata is malformed")
        if target in observed_targets or cache_path in observed_sources:
            raise ValueError("Python wheel notice source or target is duplicated")
        observed_targets.add(target)
        observed_sources.add(cache_path)
        observed_distributions.add(canonical)

    if observed_distributions != set(locked):
        raise ValueError("Python wheel notice distributions differ from the locked 60 distributions")
    if packaged_notice_paths is not None and observed_targets != packaged_notice_paths:
        raise ValueError("packaged Python wheel notice files differ from the inventory")
    if packaged_notice_hashes is not None:
        for entry in components:
            target = entry.get("file", entry.get("bundle_target", entry.get("notice_source")))
            digest = entry.get("sha256", entry.get("evidence_sha256"))
            if packaged_notice_hashes.get(target) != digest:
                raise ValueError("packaged notice hash differs from the inventory")
