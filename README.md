# LoreRim Autoinstall

One-click installer for the [LoreRim](https://www.lorerim.com/) Wabbajack modlist on Linux.

Pick your install and download folders, sign in to Nexus Mods, click **INSTALL LORERIM**, and walk away. The app handles everything end to end:

- Preflight checks: Steam, Skyrim Special Edition (appid 489830), protontricks, GE-Proton, ~600 GB of disk space
- Nexus Mods sign-in via browser (OAuth); fully unattended downloads with Nexus Premium
- Downloads and installs the modlist with a bundled [jackify-engine](https://github.com/Omni-guides/dev-jackify-engine) (native-Linux Wabbajack)
- Adds a **LoreRim** shortcut to your Steam library pointing at Mod Organizer 2, assigns GE-Proton, creates the prefix, and installs prerequisites via protontricks

## Requirements

- Native Steam (not Flatpak/Snap) with **Skyrim Anniversary Edition** (English) installed
- [protontricks](https://github.com/Matoking/protontricks)
- GE-Proton in `compatibilitytools.d` (install with [ProtonUp-Qt](https://davidotek.github.io/protonup-qt/))
- ~250 GB free for downloads + ~330 GB free on an SSD for the install
- A Nexus Mods account (Premium strongly recommended — free accounts must click every download manually)

## Install

Grab the AppImage from [Releases](../../releases), make it executable, run it:

```sh
chmod +x LorerimAutoinstall-x86_64.AppImage
./LorerimAutoinstall-x86_64.AppImage
```

## Building from source

```sh
dotnet publish src/Lorerim.Gui -c Release -r linux-x64 --self-contained
scripts/setup-deps.sh <publish dir>   # fetches the pinned jackify-engine
scripts/build-appimage.sh             # produces the AppImage
```

For development, extract a jackify-engine release anywhere and set `LORERIM_ENGINE_PATH` to its directory.

## Credits

- [Wabbajack](https://www.wabbajack.org/) and the LoreRim team
- [Omni-guides](https://github.com/Omni-guides/Jackify) for Jackify and jackify-engine, whose workflow this app follows
- Architecture based on the STALKER GAMMA Linux GUI

## License

GPL-3.0 (same as Wabbajack and jackify-engine).
