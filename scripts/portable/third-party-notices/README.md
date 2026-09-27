# Portable third-party notice inputs

This directory contains reviewed notice inputs for the Windows x64 portable bundle. It does not make a legal determination or change this repository's MIT license.

The bundle collector copies each non-gap `notice_source` verbatim, verifies its recorded SHA-256, and marks the generated notice inventory complete only when every component has verified evidence and no `is_gap` entries. A gap marker is emitted while that condition is false. A complete notice inventory is a packaging check, not a legal opinion, product release approval, signature, or source-authentication claim.

The Python runtime lock pins 60 distributions and the cache contains 93 license/notice-like files under their `.dist-info` directories. All 93 files are inventoried with their exact distribution, version, cache-relative source, bundle target, and SHA-256; every `License-File` reference has a matching file. Playwright's driver `ThirdPartyNotices.txt` is separately inventoried because it is outside `.dist-info`. Avalonia 12.1.2's MIT text is from the exact repository commit recorded by its cached nuspec, and the collector accepts only its pinned source path and hash.

## Chromium 143 / Playwright r1200

`chromium-143-r1200-credits.html` is an unmodified DOM snapshot from `chrome://credits` on the exact locally verified Chromium binary. The browser lock pins Playwright 1.57.0, revision r1200, browser version 143.0.7499.4, and archive SHA-256 `4b4d412c65ffa6486eebdeb7ca05186c9598f90a60fb67e201fb8afa27776ab6`. The extracted executable SHA-256 is `98da1bd10d317fd533c357dfcb374bb9c8c23d28c11c0a04bbd550d2add95e2d`; the credits snapshot is 14,908,908 bytes with SHA-256 `baac582df59212838afdd377fd757412e79d3865d7ff2e7c2b9f898e6513cd6b`.

The DOM contains 560 `div.license pre` entries. Chromium's own page has one empty entry, `Headers for ONNX Runtime C/C++ APIs`; the snapshot preserves it unchanged. This component is not an unshipped placeholder: Chromium's v1.23.0 roll records it as MIT, names `src/LICENSE`, and says `Shipped: Yes`. The roll pins Microsoft ONNX Runtime commit `be835efc56aca19b8e810538ec93c8e150e0fc61`; its `Update.py` retrieves `LICENSE` from that exact revision. The raw file was fetched from that immutable URL and is included byte-for-byte as `onnxruntime-headers-v1.23.0-MIT.txt` (1,073 bytes, SHA-256 `2f07c72751aed99790b8a4869cf2311df85a860b22ded05fa22803587a48922c`).

Evidence links:

- [Chromium 143.0.7499.4 release tag](https://chromium.googlesource.com/chromium/src/+/refs/tags/143.0.7499.4)
- [Chromium ONNX Runtime headers v1.23.0 roll](https://chromium.googlesource.com/chromium/src/%2B/4838ea61d7f27ac430fa1e59dfb2dcc471c9cc3f%5E%21/)
- [Pinned ONNX Runtime LICENSE source](https://raw.githubusercontent.com/microsoft/onnxruntime/be835efc56aca19b8e810538ec93c8e150e0fc61/LICENSE)
- [Chromium third-party credits generation guidance](https://chromium.googlesource.com/chromium/src/%2Bshow/main/docs/adding_to_third_party.md)

The roll is at Chromium main commit position `#1523067`; the 143.0.7499.4 branch point is `#1536371`, so this component metadata predates and is included in the matching release source line. The Chrome credits HTML itself is preserved exactly; its empty ONNX entry is paired with the exact MIT source file rather than edited or filled by hand. The current local inventory has no known gaps after these two inputs are verified. Re-run the locked bundle collector and release scanner before any public release claim.
