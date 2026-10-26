# BARAS boss encounters

These files are the boss encounter definitions from [BARAS](https://github.com/baras-app/baras) (`core/definitions/encounters`),
copied unchanged from commit `ad64d276b1f8` (2026-10-15). Tactical Readout reads them directly for its boss timers, phases
and alerts.

They are under the MIT license in `LICENSE`, copyright The BARAS Authors.

Don't edit them here. Fixes belong upstream in BARAS; refresh this folder with
`python tools/update_baras.py`. To change one for yourself, copy it to the same place under your own fights folder
(`%APPDATA%\Ashla\pluginConfigs\TacticalReadout\Fights\BARAS\...`); a file there replaces the built-in one.
