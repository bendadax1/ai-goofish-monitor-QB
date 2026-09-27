# Windows portable third-party inventory

This is an engineering inventory for release packaging, not legal advice or a license change. The repository remains MIT licensed.

The machine-readable source is [inventory.json](../scripts/portable/third-party-notices/inventory.json). It was checked against only the approved local runtime inputs: the P0 NuGet cache, the locked embedded Python runtime, the Playwright browser component, and the PostgreSQL component.

Current packaging requirements:

- Copy verified source notice/license files verbatim and check listed hashes.
- Keep the original copyright and notice text; do not replace it with a license label.
- Do not mark a release notice-complete while any `is_gap` entry remains true.
- The current gaps are individual material collection for 60 locked Python distributions and Chromium credits from the exact r1200 component. Chromium's executable was located, but standalone credits material was not found in the approved local component tree.

No dependency was added, downloaded, or changed for this inventory.
