# Changelog

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
