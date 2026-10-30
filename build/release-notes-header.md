VitaSharp is an experimental PlayStation Vita emulator. Every package is self-contained: no .NET install is needed.

**Which file?** Windows: `-setup.exe` (installer) or `.zip`. Linux: `.AppImage` or `.tar.gz`. macOS: `.dmg` or
`.app.zip` (`osx-arm64` for Apple Silicon, `osx-x64` for Intel). Files without a suffix are Native AOT builds (faster
start, smaller); `-readytorun` files are ReadyToRun builds, which also have the `il` CPU backend. `SHA256SUMS.txt`
lists the checksums (`sha256sum -c SHA256SUMS.txt --ignore-missing`).

**Firmware is not included.** Games run without it. For the system fonts, the few libraries VitaSharp does not
reimplement and the real PS Vita system, install the official system update (`PSVUPDAT.PUP`) that you download from
PlayStation's site: launcher **Configurações > Geral > Firmware > Instalar firmware (.PUP)…** or
`VitaSharp.Desktop install-firmware <PSVUPDAT.PUP>`. Bring your own games too; none are included.

**The binaries are not signed.**
- Windows: SmartScreen warns about the download; choose **More info** and then **Run anyway**.
- macOS: Gatekeeper blocks the app. Run `xattr -dr com.apple.quarantine VitaSharp.app` (or right-click it and choose
  Open); on Apple Silicon also ad-hoc sign it: `codesign --force --deep --sign - VitaSharp.app`.

**System requirements**
- Windows 10 or 11 (x64), Linux (x64 or ARM64, glibc, X11 or Wayland) or macOS (Apple Silicon or Intel).
- A GPU with a Vulkan 1.3 driver. Linux: the Vulkan loader (`sudo apt install libvulkan1 mesa-vulkan-drivers`).
  macOS: MoltenVK 1.3 or later (`brew install molten-vk vulkan-loader`, or the LunarG Vulkan SDK) and
  `brew install ffmpeg` for videos and MP3/AAC audio.
- A modern multi-core CPU.
