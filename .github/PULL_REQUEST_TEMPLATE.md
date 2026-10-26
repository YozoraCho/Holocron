## Plugin

- Name and version:
- Source code:
- What it does:
- Does it use the network? If so, what does it send and where:

## Checklist

- [ ] I ran `python tools/holocron.py add` and this pull request only changes `plugins/<Id>.json` and `repo.json`.
- [ ] The zip was built from the source above, with the build's `manifest.json`, and not edited afterwards.
- [ ] The plugin doesn't inject into SWTOR, read or write its memory, hook functions, intercept traffic, send input
      to the game, or make up data.
- [ ] The plugin doesn't download or run code that isn't in its zip.
- [ ] The source is public under a license listed in the README.
