# Tactical Readout

Tactical Readout (`src/TacticalReadout`) shows boss timelines and alerts over SWTOR, in the spirit of
[Cactbot](https://github.com/OverlayPlugin/cactbot)'s raidboss module for FFXIV. Like everything in Ashla, it only
reads the combat log: every timeline and alert comes from what the log records.

It has three windows:

- **Tactical Readout**: the timeline. Upcoming boss abilities as countdown bars, the fight's clock, and the result
  when it ends ("Victory after 4:32"). Right-click it for settings and to reload fight files.
- **Tactical Readout Alerts**: big text in the middle of the screen. It's invisible until an alert shows; hover it
  to see where it is, then drag it or resize it from a corner.
- **Tactical Readout Settings**: open it from the Ashla window's Panels list or the timeline's right-click menu.
  Fight files and their problems, text-to-speech, the timeline's look-ahead, and Make a Timeline.

Alerts come in three levels, like Cactbot: **info** (white), **alert** (amber) and **alarm** (red). With
text-to-speech on (Settings > Alerts, Windows only), alerts are also spoken in the Windows voice; an alarm cuts off
anything still being spoken.

## Making a fight

1. Turn on combat logging in SWTOR and pull the boss. Ashla saves the fight when you leave combat.
2. Open Tactical Readout Settings > **Make a Timeline** and press **Make** next to the fight. Tactical Readout writes
   a starter file to your fights folder (`%APPDATA%\Ashla\pluginConfigs\TacticalReadout\Fights`): the area and
   difficulty, the boss, every ability the boss used with its time, and a commented-out trigger for each effect
   the boss put on you. Abilities used very often (basic attacks) are left out and listed in a comment.
3. Edit the file: delete abilities nobody cares about, give the rest clear names, add triggers, and set `window`
   and `jump` where the fight branches or loops. Press **Reload** in Settings > Fights.
4. Pull again. Tactical Readout keeps the timeline in step with the boss: every time the boss uses an ability that
   has a `sync`, the timeline moves to that line.

A few pulls are better than one: phases pushed by damage move around between pulls, and comparing starter files
shows which abilities are on a fixed timer.

## Fight files

A fight file is a `.txt` file. Blank lines are ignored and `#` starts a comment. Text goes in double quotes
(`\"` for a quote inside); ids are the numbers in `{...}` in the combat log. Ids are better than names: names
change with the game's language, ids don't.

```
encounter "Test Boss"
area 833571547775000          # where the fight is
difficulty "8 Player Veteran" # optional; leave out to match every difficulty
boss 4000000000000001         # who the fight is with

hideall "--sync--"

0 "Pull"
10 "Big Slam" sync ability 5000000000000001 from boss window 4
20 "--sync--" sync ability 5000000000000004 from boss
30 label "loop"
40 "Swipe" sync ability 5000000000000003 from boss duration 3
50 "Swipe again" sync ability 5000000000000003 from boss jump "loop"

trigger "slam soon"
  before "Big Slam" 3
  alert "Slam in 3"
  speak "slam"
end

trigger "shock"
  match apply 5000000000000002 on me
  alarm "{effect} on you, move!"
  suppress 5
end
```

(The ids above are made up; use the ones from your own logs.)

### The header

| Line | Meaning |
|---|---|
| `encounter "Name"` | Required, first. The name shown on the timeline. |
| `area <id or "name">` | The area the fight is in. Repeat for more than one. No area line means any area. |
| `difficulty <id or "name">` | The difficulty from the area line in the log, like `"8 Player Veteran"`. Optional. |
| `boss <id or "name">` | An NPC the fight is with. Repeat for fights with several bosses. |
| `start <event>` | Optional. By default the fight starts when you enter combat and a boss is involved, timed from your combat start. A `start` line starts it on that event instead. |
| `end <event>` | Optional. By default the fight is won when every boss that showed up has died. |
| `hideall "Text"` | Timeline lines with this text sync the timeline but aren't shown. |

A fight also ends when you leave combat while alive, when you change area, or when you're dead and no boss has
done anything for 30 seconds (a wipe). Dying alone doesn't end it, so a battle rez picks the timeline up again.

### Timeline lines

```
<seconds> "Text" [sync <event>] [window <before>[,<after>]] [jump <seconds or "label">] [duration <seconds>]
<seconds> label "name"
```

- **seconds**: when it happens, counted from the start of the fight.
- **sync**: when this event happens close to the line's time, the timeline jumps to the line's time. "Close" is
  the window: 2.5 seconds before and after by default, or `window 10` (10 both ways), or `window 30,5`.
- **jump**: after syncing, go to this time or label. For loops and phase changes. A jump needs a sync.
- **duration**: keep the bar on screen (green, "now") for this long after its time, for things that last.
- **label**: a named point to jump to.

### Events

Syncs, triggers and `start`/`end` lines match events from the log:

| Event | Matches |
|---|---|
| `ability <id or "name">` | An ability being used (AbilityActivate). |
| `apply <id or "name">` | An effect being applied (not damage or healing). |
| `remove <id or "name">` | An effect being removed. |
| `damage <id or "name">` | Damage from that ability. |
| `heal <id or "name">` | Healing from that ability. |
| `interrupt <id or "name">` | That ability being interrupted. |
| `death <who>` | Someone dying. |
| `combat` | You entering combat. |

Use `any` in place of the id or name to match any ability or effect. After the event, add `from <who>` (who did
it) and `on <who>` (who it happened to). `<who>` is `me`, `boss`, `any`, an NPC id or a "name". For example:
`apply "Corrosive Grenade" from boss on me`, `ability 5000000000000001 from boss`, `death boss`.

The combat log only records events that involve you or your companion, plus what NPCs do around you, so
something that happens only to another player can't be matched.

### Triggers

```
trigger "id"
  match <event>              # or: before "Timeline text" <seconds>
  info "Text" | alert "Text" | alarm "Text"
  speak "Text"               # optional: what to say instead of the text
  silent                     # optional: never speak this one
  delay <seconds>            # optional: wait before showing
  duration <seconds>         # optional: how long it stays (default 4)
  suppress <seconds>         # optional: ignore repeats for this long
end
```

- `match` triggers fire on an event during the fight.
- `before` triggers fire that many seconds before every timeline line with that text, like Cactbot's timeline
  triggers. After a jump back, they fire again.
- Text can include `{source}`, `{target}`, `{ability}` and `{effect}` from the event that fired it.

Each trigger needs a match or before line, and a text or speak line.

## Where fight files live

- **Built-in**: `src/TacticalReadout/Fights/*.txt` in Holocron, shipped in the plugin's folder (and
  covered by its manifest hashes).
- **Yours**: `%APPDATA%\Ashla\pluginConfigs\TacticalReadout\Fights`. A file here with the same name as a built-in
  file replaces it.

Files are read when the plugin starts, when you change area and when you press Reload. A file with a mistake isn't
loaded; Settings > Fights lists the file, the line and what's wrong, and Ashla's log has it too.

To share a fight, open a pull request that adds it to `src/TacticalReadout/Fights` in Holocron. Include which
pulls it was made from.

## Compared with Cactbot

| Cactbot | Tactical Readout |
|---|---|
| Timeline `.txt` with `sync /regex/` on log lines | Timeline lines with `sync <event>` on parsed combat-log events, matched by id |
| `window`, `jump`, `duration`, `label`, `hideall` | The same |
| Triggers in JavaScript/TypeScript | Triggers in the same text file, no code, so fights can be shared safely |
| `infoText`, `alertText`, `alarmText`, `tts` | `info`, `alert`, `alarm`, `speak` |
| `timelineTriggers` with `beforeSeconds` | `before "Text" <seconds>` |
| `delaySeconds`, `durationSeconds`, `suppressSeconds` | `delay`, `duration`, `suppress` |
| `make_timeline` from a network log | Make a Timeline from a saved fight |
| Raid emulator | Every saved fight can be made into a timeline; replaying a fight through the plugins is on the roadmap |
