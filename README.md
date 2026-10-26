# Holocron

Holocron is the official plugin list for [Ashla](https://github.com/YozoraCho/Ashla), the open-source overlay
and combat-log toolkit for Star Wars: The Old Republic. Ashla reads [`repo.json`](repo.json) from this repository
to fill **Settings > Plugins > Browse**, to install plugins and to tell players when an update is out.

Every plugin here has public source code and has been reviewed by a maintainer before it was listed. Ashla marks
plugins from anywhere else as "Not reviewed by Holocron".

## Official plugins

The official plugins are developed here, in `src/`:

| Plugin | What it does |
|---|---|
| [Combat Meter](src/CombatMeter) | An ACT-style parser: damage, healing, damage taken and threat per encounter, with ability breakdowns and saved fights. |
| [Tactical Readout](src/TacticalReadout) | Boss timers, phases and alerts for 54 operations and flashpoints from [BARAS](https://github.com/baras-app/baras)'s boss data, plus Cactbot-style fight files of your own, with optional text-to-speech. Guide: [docs/tactical-readout.md](docs/tactical-readout.md). |

They build against the `Ashla.SDK` and `Ashla.UI` NuGet packages (versions in `Directory.Packages.props`), with
the .NET 10 SDK:

```
dotnet build Holocron.slnx
dotnet test Holocron.slnx
```

To try them in a developer build of Ashla, add
`-p:AshlaPluginsDir=<Ashla>/src/Ashla.Host/bin/Debug/net10.0/plugins` and every build is copied there.

Tactical Readout ships BARAS's boss encounter files (MIT) in `src/TacticalReadout/Fights/BARAS`;
`python tools/update_baras.py` refreshes them from BARAS.

To release one, raise its `<Version>` in its `.csproj`, merge, and push a tag made of its folder name and version,
such as `CombatMeter-v0.1.0`. The Plugin release workflow checks the tag, runs the tests, builds the zip with
`scripts/package-plugin.ps1` and attaches it to a GitHub release here. The run's summary has the `add` command
for the release; run it on a new branch and open a pull request like any other plugin.

## What gets listed

A plugin is listed when it:

- follows Ashla's rules: no injection, no reading or writing game memory, no hooks or traffic interception, no
  input sent to the game, and no made-up data,
- has public source code, under a license compatible with the AGPL-3.0 (Ashla's license),
- is published as a release zip built from that source, with the `manifest.json` from the build,
- works with a released version of Ashla,
- doesn't download or run code that isn't in its zip, and doesn't send data anywhere without saying so in its
  description and asking the player first.

Accepted licenses (SPDX ids): `AGPL-3.0-only`, `AGPL-3.0-or-later`, `GPL-3.0-only`, `GPL-3.0-or-later`,
`LGPL-3.0-only`, `LGPL-3.0-or-later`, `Apache-2.0`, `MPL-2.0`, `MIT`, `BSD-2-Clause`, `BSD-3-Clause`, `ISC`,
`Zlib`, `0BSD`, `Unlicense`. If yours isn't here, open an issue.

[Writing Ashla plugins](https://github.com/YozoraCho/Ashla/blob/main/docs/plugins.md) covers how to build one.

## Submitting a plugin

1. Build your plugin with Ashla's plugin build targets so its output folder has a `manifest.json`.
2. Zip the output folder. The files can sit at the root of the zip or inside one folder. Leave out `.pdb` files
   if you like; don't edit anything after building.
3. Attach the zip to a release on your plugin's repository (GitHub releases work well) and copy the zip's
   download address.
4. Fork this repository and run, with Python 3.10 or newer:

   ```
   python tools/holocron.py add <zip address> --license AGPL-3.0-or-later --changes "First release."
   ```

   This downloads the zip, checks it, writes `plugins/<Id>.json` from its `manifest.json`, and rebuilds
   `repo.json`. Use `--source` if your source code isn't at the `Repository` address in your manifest.
5. Open a pull request with those two files: `plugins/<Id>.json` and `repo.json`.

A check runs on every pull request. It downloads your zip again and makes sure the checksum, version, API level
and file hashes match the entry. A maintainer then reads your source and either merges or asks for changes.

### Entry files

Each plugin has one file in `plugins/`, named after its id. `add` fills it in; you only write the license, the
source address and the changes line yourself.

```json
{
  "Id": "CombatCounter",
  "Name": "Combat Counter",
  "Author": "Someone",
  "Description": "Counts combat events.",
  "Version": "1.0.0",
  "ApiLevel": 1,
  "Tags": ["combat"],
  "License": "AGPL-3.0-or-later",
  "Source": "https://github.com/someone/CombatCounter",
  "Download": "https://github.com/someone/CombatCounter/releases/download/v1.0.0/CombatCounter-1.0.0.zip",
  "Sha256": "<SHA-256 of the zip>",
  "Files": {
    "CombatCounter.deps.json": "<SHA-256>",
    "CombatCounter.dll": "<SHA-256>"
  },
  "Changes": "First release."
}
```

`Name`, `Author`, `Description`, `Version`, `ApiLevel`, `Tags` and `Files` must match the zip's
`manifest.json`. `Changes` is one line (up to 300 characters) shown next to the update in Ashla.
[`schema/plugin.schema.json`](schema/plugin.schema.json) describes every field.

## Updating a plugin

Publish a new release zip with a higher version, then run `add` again with the new address:

```
python tools/holocron.py add <new zip address> --changes "What's new in this version."
```

The license and source address carry over. A new download always needs a higher version than the one listed,
and the version can't go back. Open a pull request with the changed entry and `repo.json`; updates are reviewed
like new plugins.

Players are told about the update the next time Ashla starts, and install it from Settings > Plugins.

## How Ashla checks what it installs

When a player installs a plugin, Ashla:

- downloads the zip over https (up to 50 MB) and checks its SHA-256 against `repo.json`,
- unpacks it into a temporary folder, refusing paths outside the folder, more than 500 files or more than 200 MB,
- checks the plugin's `manifest.json`: the id, an API level this Ashla supports, every listed file present with
  the same hash, and every DLL listed,
- checks that the manifest's version and file hashes are the ones published here,
- then swaps the folder into place, keeping the old copy until the new one is in.

Before loading any plugin, Ashla checks the manifest again, so a file changed after install stops the plugin
from loading.

## Running your own repository

Anyone can host a list in the same format and players can add it under **Settings > Plugins > Repositories**.
Ashla warns them that those plugins aren't reviewed by Holocron. To make one, copy `tools/holocron.py` and the
`plugins/` layout, add entries with `add`, run `python tools/holocron.py build`, and host `repo.json` over https
(a raw GitHub address works).

## Tools

```
python tools/holocron.py add <zip address> [--license ID] [--source URL] [--changes TEXT]
python tools/holocron.py validate [entries...] [--download] [--base REF] [--check-index]
python tools/holocron.py build [--check]
python -m unittest discover -s tools
```

- `validate` with no arguments checks every entry's fields without downloading. Name entries, or pass
  `--download` for all of them, to download and check their zips too. `--base origin/main` checks the entries
  changed since that ref, including the version rules; this is what pull requests run.
- `build` writes `repo.json` from `plugins/`, sorted by id (`add` does this for you). `--check` fails instead of
  writing if it's out of date.

## For maintainers

Before merging a plugin:

- the check on the pull request passed,
- the source at `Source` builds the release that was submitted (same version, no extra DLLs),
- the code follows the rules above: no process access, memory reads, hooks, packet capture, input automation,
  hidden network calls or code downloaded at run time,
- the description says plainly what the plugin does and anything it sends over the network.

The Validate workflow runs on every pull request and on `main`. It fails if `repo.json` doesn't match
`plugins/`, so what's merged is what Ashla reads; nothing pushes to `main` on its own. The Build plugins workflow
builds and tests `src/`. Protect `main` with a ruleset that requires a pull request and both checks (`validate`
and `build`).

When Ashla raises its plugin API level in a release, raise `API_LEVEL` in `tools/holocron.py` once that release
is out. Raise `FORMAT` only together with an Ashla release that reads the new format; older versions of Ashla
refuse a newer format and keep showing their last saved copy of the list.
