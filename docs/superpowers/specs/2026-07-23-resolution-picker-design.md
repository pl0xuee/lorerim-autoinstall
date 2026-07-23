# Resolution check and picker — design

Date: 2026-07-23
Status: approved, ready for an implementation plan

## Problem

LoreRim ships a fixed render resolution and says nothing about the display it will
run on. A completed install on this machine sits at:

```
profiles/{Default,Ultra,Extreme}/skyrimprefs.ini
  iSize W =3840
  iSize H =2160
  bBorderless =1
  bFull Screen =0
```

while the primary display is a 3440x1440 ultrawide. With SSE Display Tweaks
configured `Borderless = true` and `BorderlessUpscale = true`, the game renders
at 4K 16:9 and upscales onto a 21:9 panel — paying for pixels it cannot show and
getting the aspect ratio wrong.

The list is otherwise already set up for ultrawide: `Norden UI 21x9`,
`Lorerim 21x9 Configs`, `Dear Diary Dark Mode 21x9` and their siblings are
enabled in `modlist.txt`. Only the resolution disagrees, and nothing surfaces
that.

## Goal

Detect the resolutions the machine can actually display, let the user choose
one, write it into the install, and warn when the configured resolution matches
no connected display. Doing nothing must remain the default: an install where
the user never touches this is left exactly as the modlist ships it.

## Detection

`/sys/class/drm/<card>-<connector>/` is the source for modes:

- `status` — `connected` / `disconnected`
- `enabled` — whether the compositor is driving it
- `modes` — every supported mode, newest-first, e.g. `3440x1440`

This is session-agnostic (works under Wayland, X11 and neither), needs no
external binary, and reports *supported* modes.

Primary output comes from `xrandr --query`'s `primary` flag when xrandr exists.
When it does not, the connector with the largest native mode is used and flagged
as a guess rather than presented as fact.

### Why not xrandr for the mode list

xrandr reports the *current layout*, not capability. On this machine:

```
DP-1 connected primary 3440x1440+0+1440     native 3440x1440   ✓
DP-2 connected          2560x1440+487+0     native 3840x2160   ✗ 4K hidden
DP-3 connected          1440x2560+3440+320  native 2560x1440   ✗ reported rotated
```

An xrandr-derived list would omit the 4K mode DP-2 supports and offer DP-3's
portrait orientation. Per-desktop tools (`kscreen-doctor`, `wlr-randr`) each
cover one compositor family and are not worth depending on.

## Writing

Target: `profiles/<profile>/skyrimprefs.ini`, the file MO2 redirects the game's
prefs to. Format facts that constrain the writer:

- UTF-8 **with BOM**
- **CRLF** line terminators
- Key style `iSize W =3840` — space before `=`, none after

The writer matches the existing `iSize W` / `iSize H` lines with a regex
capturing the key and its exact spacing, and replaces only the digits. BOM,
line endings, comments, ordering and every unrelated line survive byte-for-byte.
A parse-and-rewrite would normalise formatting across a BethINI-generated file
for no benefit and risk churn the next time BethINI runs.

Writes go to **every profile** in the install, not just the active one.
Switching profile in MO2 would otherwise silently revert the resolution, which
presents as the setting not having worked.

## Components

### `DisplayCatalog` (new, `Services/Display/`)

```
IReadOnlyList<DisplayOutput> Scan()          // connector, native mode, all modes, primary
IReadOnlyList<ResolutionChoice> Choices()    // deduped, labelled by offering display
```

The sysfs root is a constructor parameter defaulting to `/sys/class/drm`, so
tests run against fixture trees instead of the host. Host-dependent scanning is
what made `CompatToolCatalog` untestable until it was made injectable.

### `SkyrimResolutionService` (new, `Services/Modlist/`)

```
(int W, int H)? Read(string installDir)      // from the active profile
void Apply(string installDir, int w, int h)  // to every profile
```

### Settings

`AppSettings.PreferredResolution` — `"WxH"` or null. **Null means "leave the
modlist's value alone"** and is the default, so an install proceeds untouched
unless the user opts in.

## Flow

The install page captures the choice before the run, but profiles do not exist
until the engine finishes, so the write happens after it — alongside the
existing compatibility pass, which is already the place where post-install
modlist edits belong. The Settings control applies the same writer to an
existing install on demand. One writer, two entry points, one defined moment of
application.

## The check

Compare the install's current `iSize` against the native modes of connected
displays. No match produces a warning naming both sides, e.g. "configured for
3840x2160; no connected display is that resolution (primary is 3440x1440)".
This fires on the current install.

## Out of scope

Toggling the 21x9 mod group in `modlist.txt`. Enabling and disabling mods is
MO2's job and a bad write there breaks the install. The picker may *report*
that the chosen aspect ratio disagrees with the enabled UI patches; it will not
act on it.

Changing `bFull Screen` / `bBorderless`, or SSE Display Tweaks' `Resolution` and
`BorderlessUpscale`. The modlist's display mode is deliberate and only the
render resolution is in question here.

## Error handling

| Condition | Behaviour |
|---|---|
| No install present | Picker disabled, explaining an install is needed first |
| `/sys/class/drm` unreadable or empty | Fall back to xrandr's reported modes; if that fails too, show the current value only and say detection failed |
| xrandr absent | Mode list still works; primary is a flagged guess |
| `iSize` keys missing from a profile INI | Insert them into the `[Display]` section |
| A profile INI is unwritable | Report which profile failed; do not half-apply silently |

## Testing

`DisplayCatalogTests` — fixture sysfs trees: connected vs disconnected, empty
`modes`, a rotated panel, multiple connectors sharing a resolution (dedup), and
no xrandr available.

`SkyrimResolutionServiceTests` — fixture INIs asserting the BOM survives, CRLF
survives, neighbouring lines are untouched byte-for-byte, key spacing is
preserved, missing keys are inserted, and all profiles are written.

Regression case for the machine that prompted this: an install reading
3840x2160 with a 3440x1440 primary produces a mismatch warning.
