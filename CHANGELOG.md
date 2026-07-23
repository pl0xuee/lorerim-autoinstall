# Changelog

## Unreleased

- The install page and Settings now offer a game resolution, and report when the installed one matches no connected display. LoreRim ships 3840x2160, which on a 3440x1440 ultrawide means rendering 4K 16:9 and upscaling onto a 21:9 panel with nothing saying so
- Resolutions come from `/sys/class/drm` rather than xrandr, because xrandr reports the current layout: a 4K panel being driven at 1440p appears as a 1440p panel, and a rotated one appears portrait. Every distinct resolution across all displays is offered, labelled with the displays providing it, with the primary's native mode first
- The choice is written to every MO2 profile, so switching profile does not silently revert it. Only the digits in the existing `iSize` lines are replaced, leaving BethINI's BOM, CRLF terminators and formatting untouched
- Leaving the setting alone remains the default: an install where the resolution was never chosen is not modified

## v0.1.3

Stops a Proton problem from costing you a finished install.

- The modlist compatibility pass now runs even when an earlier Steam setup step fails. It was last in the sequence, so a Steam problem it has nothing to do with meant a fully installed 300 GB modlist was left with the crashing JContainers DLL still in place. A cancelled run is the one case that still skips it, and a failure inside the pass is logged rather than allowed to mask the Steam failure that caused it
- The disk-space preflight no longer blocks a re-run over an existing install. It compared free space against the size of a *fresh* install — 600 GB when both folders share a drive — without accounting for the install and downloads already sitting there, so updating a working setup demanded room for a second copy of it. With an install already present the shortfall is now a warning that says the engine only downloads what changed; a genuinely full disk (under 20 GB free) still fails
- Unit tests no longer write into the real application log at `~/.config/lorerim-autoinstall/logs/`, which was interleaving test lines into the record used to diagnose installs
- A Proton build whose Steam Linux Runtime is not installed no longer breaks the install. Proton runs inside the runtime its `toolmanifest.vdf` pins, so GE-Proton10-34 without Steam Linux Runtime 3.0 (sniper) fails at the protontricks step — after the engine has already installed 300 GB. Selection now skips builds whose runtime is missing and falls through to the newest usable GE-Proton, then Proton-CachyOS, then any build that can run
- Preflight reports the substitution and its cause before the install starts, and fails only when nothing on the machine can run at all. A manual Proton pin whose runtime is missing falls through the same chain rather than reproducing the failure
- Proton-CachyOS is ranked as a last resort instead of simply unsupported: still not what LoreRim is tested against, but reachable when no GE build can run

## v0.1.2

Fixes a crash on launch that a completed install could not avoid on its own.

- Steam setup now ends with a Linux compatibility pass over the installed modlist, and the same pass runs directly when Steam setup is switched off so it cannot be skipped
- JContainers SE's DLL is replaced with the author's patched build: every Nexus release up to 4.2.9.0 crashes under Proton, taking Wheeler and anything else built on JContainers down with it. Only installs on the SKSE runtime the patched build targets are touched
- The shipped DLL is kept beside the new one as `.nexus.bak`, written atomically so an interrupted run cannot leave a truncated backup that a later run would trust. Re-running the pass on a patched install does nothing
- Both the downloaded archive and the extracted DLL are checked against pinned SHA-256 hashes, so nothing unverified reaches the extractor or the game folder. The download is size-capped, retried on transient failures, and given an explicit timeout rather than surfacing as a cancelled install
- Extraction reuses the 7-Zip binary the jackify-engine already bundles, so the fix adds no new dependency
- Mod folders are matched case-insensitively: archives authored on Windows land as `skse/plugins` or `root` on a case-sensitive filesystem, and a missed DLL would have meant a crash on launch with nothing in the log to explain it. Each outcome — not present, unsupported SKSE runtime, already patched, patched now — is logged distinctly, and an install that needs the fix but cannot apply it fails loudly instead of skipping

## v0.1.1

Bug-fix and polish release after a full code review and security sweep.

- Fixed a UI freeze/deadlock during Nexus sign-in (blocking token write and slow xdg protocol registration on the UI thread)
- Proton selection now enforces LoreRim's tested build: GE-Proton10-34 ranks first, Proton-CachyOS/Valve Proton are flagged as unsupported, and an unsupported manual pin is overridden with a log note
- Disk-space preflight understands shared filesystems: when the install and download folders live on one volume, their ~590 GB combined requirement is checked together (also applied to the live catalog-size re-check)
- Settings and Install pages no longer overwrite each other's directory edits; the Proton pin is only saved when you actually change the dropdown
- Install page reworked: preflight runs automatically with a colour-coded checklist, install steps show pending/running/done glyphs (and reset properly after cancel/failure), Browse buttons on all path fields, readable INSTALL button with a Cancel beside it while running
- Log pane follows the tail so errors are visible when it opens; engine timeouts are reported as failures instead of "cancelled"
- Update check fixed (GitHub API requires a User-Agent); manual-download prompts show the mod name with an Open button
- Nexus token file is created 0600 from the first byte

## v0.1.0

Initial release.

- One-click LoreRim install: preflight checks → Nexus OAuth sign-in → bundled jackify-engine download/install → Steam shortcut, GE-Proton, prefix and protontricks setup
- Nexus OAuth (browser) with API-key fallback
- Re-running install updates/repairs an existing LoreRim in place
- In-app self-update from GitHub Releases
