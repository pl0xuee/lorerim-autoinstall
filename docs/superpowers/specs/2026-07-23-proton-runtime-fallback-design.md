# Proton runtime fallback — design

Date: 2026-07-23
Status: approved, ready for an implementation plan

## Problem

LoreRim pins `GE-Proton10-34` for ENB compatibility, and the app treats that pin
as the only thing worth checking. It never checks whether the pinned build can
actually run on this machine, so an install can spend an hour and forty minutes
laying down 300 GB and then die on the step after it.

That is not hypothetical. On 2026-07-23 an install finished the engine phase at
08:28:35 and failed at 08:29:02:

```
protontricks attempt 3/3: installing fontsmooth=rgb xact ...
RuntimeError: GE-Proton10-34 is missing the required Steam Runtime.
Steam setup: Install prerequisites (protontricks) FAILED
LoreRim install: FAILED — protontricks failed after 3 attempts
```

Proton does not run on the host system. Each build is compiled against one
specific Steam Linux Runtime and executes inside that container via
pressure-vessel; `toolmanifest.vdf` pins the runtime by appid. `GE-Proton10-34`
declares `require_tool_appid 1628350` — Steam Linux Runtime 3.0 (sniper) — which
was not installed. Steam Linux Runtime 4.0 was present, but runtimes are not
interchangeable, so it could not stand in.

The machine had two other compat tools that would have worked immediately:

| compat tool | `require_tool_appid` | runtime installed |
|---|---|---|
| GE-Proton10-34 | 1628350 (sniper) | no |
| GE-Proton11-1 | 4183110 (SLR 4.0) | yes |
| proton-cachyos-slr | 4183110 (SLR 4.0) | yes |

Nothing in the app could see that, because nothing reads `toolmanifest.vdf`.

## Goal

A missing runtime must never break an install. Selection falls through to
something that works, says clearly what it substituted and why, and reports the
problem during preflight rather than after the engine has run.

## Approach

Usability is a **filter**, applied before the existing ranking. `CompatTool`
learns which runtime it requires; a selector drops tools whose runtime is absent
and then takes the head of the list `Scan` already orders.

Two properties matter here. First, `Rank` encodes LoreRim *policy* — static,
identical on every machine — while usability is *this machine's state*; keeping
them separate keeps `Rank` testable without a filesystem. Second, a filter can
express "nothing is usable", which a ranking cannot: a sort always returns a
head, even when that head cannot run.

### Alternatives rejected

- **Fold usability into `Rank`.** Smaller diff, but it conflates policy with
  machine state and makes `PickBest` hand back unrunnable tools rather than
  reporting that nothing qualifies.
- **Check before protontricks in `SteamIntegrationService`.** Same 08:29
  discovery with a better error message. The information is available at 06:47
  from a file read; spending the engine phase to learn it is the bug.
- **Try 10-34 and fall back on protontricks failure.** Catches more causes, but
  only after the engine has run, and re-runs a slow step to learn what parsing a
  manifest would have told us. Rejected on the same grounds.

## Components

### `SteamLibraries` (new, `Services/Steam/`)

`SkyrimLocator.EnumerateLibraries` moves here unchanged and becomes shared.
Walks `steamapps/libraryfolders.vdf` from the Steam root, yielding the root
first. `SkyrimLocator` is updated to call it, so there is one library-walking
implementation rather than two.

### `SteamRuntimeCatalog` (new, `Services/Steam/`)

`bool IsInstalled(int appId)` — scans every library for
`appmanifest_{appId}.acf` and requires `StateFlags & 4` (fully installed), the
same test `SkyrimLocator` already applies to Skyrim. A partially downloaded
runtime is not a usable one. Results are cached per scan.

### `CompatTool` (changed)

Gains `int? RequiredRuntimeAppId`, parsed by `CompatToolCatalog.Scan` from
`toolmanifest.vdf` in the tool directory it is already reading
`compatibilitytool.vdf` from. `null` means "declares no requirement".

### `LorerimProton` (changed)

`ProtonSuitability` gains a `LastResort` tier between `Untested` and
`Unsupported`, matching `cachyos` case-insensitively in the internal or display
name. This makes Proton-CachyOS reachable by the fallback chain while still
being described honestly in the UI.

New entry point:

```
Select(tools, Func<int, bool> runtimeInstalled, string? pinned)
  -> (CompatTool? Tool, ProtonSuitability Suitability, CompatTool? SubstitutedFor)
```

Pure, filesystem-free, and therefore testable with a fake predicate.

## Selection chain

1. Filter to usable: `RequiredRuntimeAppId is null || runtimeInstalled(id)`.
2. If a pinned tool is set and usable, take it.
3. Otherwise take the head of the usable list under the existing ordering:
   `Rank` ascending, then version descending.

The existing ordering already produces the desired chain, so no new sort is
needed:

`GE-Proton10-34` → newest usable `GE-Proton10-x` → newest usable `GE-Proton` →
Proton-CachyOS → any other usable tool.

Two decisions inside that chain:

- **Same line beats newer.** If 10-34 is unusable and both `GE-Proton10-33` and
  `GE-Proton11-1` are usable, 10-33 wins. It is in the line the ENB pin exists
  to protect. This is a deliberate deviation from "newest overall".
- **An unusable explicit pin falls through.** A pin is a preference, not a
  suicide pact; honouring one that cannot run reproduces the exact failure this
  design removes. The substitution is stated in the log and in preflight.

## Reporting

`PreflightService.ProtonCheck` uses the same selector:

- `Ok` — the pinned build is usable.
- `Warn` — a substitution happened, naming both halves and the cause:
  `GE-Proton10-34 needs Steam Linux Runtime 3.0 (sniper, appid 1628350), which
  is not installed — using GE-Proton11-1 instead.`
- `Fail` — nothing usable. This is a new failure mode and the correct one: there
  is genuinely nothing on the machine that can run.

Steam setup logs the same substitution line when it picks a tool, so the log
explains its own choice without cross-referencing preflight.

A small appid → name map (1391110 soldier, 1628350 sniper, 4183110 SLR 4.0)
makes messages readable, falling back to the bare number for anything unknown.

## Error handling

| Condition | Behaviour |
|---|---|
| `toolmanifest.vdf` missing or unreadable | Treat as no requirement (usable) |
| `require_tool_appid` absent or non-numeric | Treat as no requirement (usable) |
| `appmanifest` present, `StateFlags & 4` unset | Runtime not installed → unusable |
| Steam root not locatable | Skip usability filtering; behave exactly as today |

The bias is deliberate: an unreadable manifest must not filter out a tool that
works today. Filtering wrongly is a regression; failing to filter is the status
quo.

## Testing

`LorerimProtonTests` — chain behaviour against a fake availability predicate,
no filesystem:

- pinned build usable → selected, `Ok`
- pinned unusable, same-line usable → same-line wins over a newer major
- pinned unusable, no same-line → newest usable GE-Proton
- no usable GE-Proton → Proton-CachyOS, reported as `LastResort`
- no usable tool at all → no selection, preflight fails
- explicit pin unusable → falls through, substitution reported
- Steam unlocatable → every tool treated as usable

`CompatToolCatalogTests` (new) — manifest parsing over temp directories:
absent `toolmanifest.vdf`, malformed VDF, non-numeric appid, well-formed appid.

Regression coverage for the reported failure: a catalog of exactly the three
tools above, with 1628350 absent and 4183110 present, selects `GE-Proton11-1`.
