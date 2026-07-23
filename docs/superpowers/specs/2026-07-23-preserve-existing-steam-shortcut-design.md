# Preserve an existing Steam shortcut on a re-run — design

Date: 2026-07-23
Status: approved, ready for an implementation plan

## Problem

Applying a modlist update means re-running the installer. On that re-run the
Steam-setup phase runs again and rewrites the "LoreRim" non-Steam shortcut
(exe, start dir, launch options, icon), its compatibility-tool mapping, and its
grid artwork. A user who has customised their Steam entry does not want an
update to overwrite it, and — if they have renamed the entry away from the
matched name — a re-run could even add a second one. The install re-run should
leave an existing entry alone while still refreshing everything needed to keep
the updated modlist launchable.

Today's write is already partly idempotent: `ShortcutsVdfService.Upsert` matches
an existing entry by `AppName` (case-insensitive), reuses its AppID, and
preserves user-only fields (LastPlayTime, artwork, tags). It still rewrites the
fields it owns and re-runs the whole pipeline. The change below turns that into
"if it already exists, do not touch it at all".

## Behaviour

When the Steam-setup pipeline runs and a shortcut for the target `AppName`
already exists with a usable AppID:

- **Skip** writing the shortcut (`Upsert`), setting the compatibility tool
  (`SetCompatTool`), and installing grid artwork (`InstallGridArt`).
- **Still run** the rest of the pipeline against the existing entry's AppID:
  create/refresh the Proton prefix, run protontricks, apply compatibility fixes.
- **Still bounce Steam** — the shutdown and restart steps run as before, because
  protontricks and prefix work are more reliable with Steam down. Only the three
  sub-steps that edit the Steam game entry are skipped; the entry itself is never
  touched.

When no such entry exists, the pipeline behaves exactly as it does today (write
shortcut → set compat tool → install grid art → prefix → protontricks → fixes).

## Which callers preserve

`SteamIntegrationService` is shared by two callers. A new flag decides whether an
existing entry is preserved:

- **`InstallOrchestrator`** (the one-click install / update re-run) preserves an
  existing entry. This is the "checking for updates" path the change targets: a
  re-run must never clobber or duplicate the entry.
- **The standalone "Steam Setup" page** (`SteamSetupViewModel`) does **not**
  preserve — it still (re)writes. That page is the explicit *set up / repair my
  Steam entry* tool, so pressing its button must be able to recreate a broken or
  missing entry.

## Components

### `ShortcutsVdfService.Find` (new)

`SteamShortcut? Find(SteamInstallation steam, string appName)`

Reads `shortcuts.vdf` and returns the entry matching `appName` (case-insensitive)
whose AppID is non-zero, or null. A zero AppID cannot key a Proton prefix or a
`CompatToolMapping` entry, so it is treated as "not found" and the caller writes
normally. Matching semantics mirror the existing-entry lookup inside `Upsert`.

`List` already parses the file into `SteamShortcut` records; `Find` reuses that
parse and applies the match, so the VDF-matching logic stays inside the VDF
service.

### `SteamSetupContext.PreserveExistingShortcut` (new field)

`bool`, default `false`. `InstallOrchestrator` builds its context with `true`;
`SteamSetupViewModel` leaves the default.

### `SteamIntegrationService` step 1 (changed)

Step 1 becomes conditional:

```
if (ctx.PreserveExistingShortcut && shortcutsVdf.Find(ctx.Steam, ctx.AppName) is { } existing)
{
    shortcut = existing;               // reuse its AppID for steps 3–5
    log.Append($"Existing Steam shortcut '{ctx.AppName}' found (appid {existing.SignedAppId}); leaving it untouched.");
}
else
{
    shortcut = shortcutsVdf.Upsert(...);
    configVdf.SetCompatTool(...);
    gridArt.InstallGridArt(...);
    log.Append(...);
}
```

Steps 0 (shutdown), 2 (restart), 3 (prefix), 4 (protontricks), and 5 (fixes) are
unchanged.

## Testing

Follows the repo's convention: test the pure/file logic, leave the
process-spawning steps to manual verification.

Unit-tested (`Find` round-trips against a temp `shortcuts.vdf` written by
`Upsert`):

- Finds an entry by name, case-insensitively.
- Returns null when no entry matches the name.
- Returns null when the only name match has AppID 0.

Manually verified:

- A first install writes the shortcut; a second install (update re-run) leaves it
  untouched and still refreshes the prefix/protontricks/fixes.
- The standalone Steam Setup page still (re)writes an existing entry.

## Out of scope

- Changing what `Upsert` writes for a genuinely new or repaired entry.
- Repairing a stale compat-tool mapping or launch options on a preserved entry
  (the Steam Setup page remains the tool for that).
- The progress-bar issue reported alongside this request — tracked separately.
