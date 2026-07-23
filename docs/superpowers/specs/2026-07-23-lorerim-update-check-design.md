# LoreRim update check — design

Date: 2026-07-23
Status: approved, ready for an implementation plan

## Problem

The app can update *itself* from GitHub Releases, but it says nothing about the
modlist it installs. There is no way to learn that LoreRim has published a new
version, and nothing tells the user that re-running the install is what applies
one. The mechanism already exists — the engine diffs an existing install and
fetches only what changed — but it is undiscoverable.

## Where the version comes from

The Wabbajack catalog publishes it. `repositories.json` in the `wabbajack-tools/mod-lists`
repository maps repository names to catalog URLs and contains a `LoreRim` key
pointing at `https://raw.githubusercontent.com/biggie-boss/modlists/main/modlists.json`.
That file holds an entry whose `title` is `LoreRim`, carrying:

- `version` — e.g. `5.0.4.3`
- `dateUpdated` — e.g. `2026-07-10T04:08:09Z`

Two small HTTPS GETs (~30 KB combined), no engine process involved.

### Alternatives rejected

- **Ask the engine.** `jackify-engine list-modlists -n LoreRim -show-machine-url -show-all-sizes`
  prints only title, game, sizes and machine URL. Engine 0.5.7 has no `--json`
  flag, so there is no richer output to parse. Cannot supply a version without an
  upstream change.
- **Read the `.wabbajack` file.** Would mean downloading the modlist file to read
  its metadata, or reverse-engineering what Wabbajack leaves in the install
  directory. Heavier and less dependable than a published catalog field.

## Components

### `ModlistCatalogService` (new, `Services/Modlist/`)

Fetches and parses the catalog.

- `Task<string?> FetchLorerimVersionAsync(CancellationToken ct)` — the published
  version string, or null when it cannot be determined.
- Resolves the `LoreRim` key from `repositories.json`; if that key is absent,
  returns null rather than scanning all 135 repositories.
- Parsing is split into pure static functions — `ParseRepositoryUrl(json)` and
  `ParseVersion(json, title)` — so they are unit-testable from fixture strings
  with no network.
- Each request is capped at 5 MB (the two files total roughly 30 KB) and given a
  30-second timeout, matching the conventions established in `ModFixupService`:
  a stalled or oversized response must not hang or fill the disk, and an
  `HttpClient` deadline must not surface as a cancellation.

`dateUpdated` is deliberately not read. The banner shows versions only, so
carrying the date would be unused surface.

### `AppSettings.InstalledModlistVersion` (new field)

Nullable string, persisted in `settings.json`. Null means "not known yet".

### `ModlistUpdateService` (new, `Services/Modlist/`)

Compares recorded against published and decides what to show.

- `Task<ModlistUpdateStatus> CheckAsync(CancellationToken ct)`
- States: `NoInstall`, `UpToDate(version)`, `UpdateAvailable(installed, latest)`,
  `CheckFailed(reason)`.
- Comparison: `Version.TryParse` on both sides — `5.0.4.3` parses cleanly. If
  either side fails to parse, fall back to string inequality, treating any
  difference as an available update.

### Seeding an existing install

Installs made before this feature have no recorded version. On startup, if
`InstalledModlistVersion` is null **and** `ModOrganizer.exe` exists under the
configured install directory **and** the catalog fetch succeeded, record the
catalog version silently and report `UpToDate`. This runs once; the field is
non-null afterwards. If the fetch fails, record nothing and retry next launch.

### Recording on install

`InstallOrchestrator` stores the catalog version in settings after a successful
run. A failed fetch leaves the field null, which the seeding rule then handles on
a later launch.

## Behaviour

The check runs fire-and-forget at startup: it must never block the UI or raise a
dialog. Failures are written to the log and are otherwise invisible, because a
dead network at launch is not an error the user needs to act on. Note that this
differs from the app's own update check, which is a manual button on the Settings
page — the modlist check is automatic because a user who never opens Settings
should still learn that LoreRim moved on.

When an update is available, a banner appears on the main window showing both
versions. **Its button navigates to the Install page; it does not start the
install.** A stray click must not begin hours of work — the user still presses
INSTALL, which is the existing, unchanged update path.

## Error handling

- Network failure, non-success status, malformed JSON, missing `LoreRim` key, or
  a missing entry: log and show no banner.
- Oversized or stalled responses: abandoned via the size cap and timeout.
- A failed check never blocks startup, never modifies settings, and never
  prevents an install.

## Testing

Follows the approach used for the JContainers fix: TDD the pure logic, leave
network and UI to manual verification.

Unit-tested from fixtures, with no network:

- `ParseRepositoryUrl` — finds the `LoreRim` key; returns null when absent.
- `ParseVersion` — extracts the version; returns null for a missing entry or
  malformed JSON.
- Version comparison — newer, older, equal, and unparseable on either side.
- Seeding decision — seeds only when the version is null, an install exists, and
  the fetch succeeded; does not overwrite an existing recorded version.
- Status selection — each of the four states from its inputs.

Manually verified: the banner appears against a deliberately stale recorded
version, and the check stays silent with the network unavailable.

## Out of scope

- Background polling or checks while the app is closed
- Auto-applying an update without an explicit press
- Tracking modlists other than LoreRim
