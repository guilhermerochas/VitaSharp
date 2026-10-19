# FFmpeg

VitaSharp uses FFmpeg (LGPL build) for SceAvPlayer movies, SceVideodec H.264 and SceAudiodec MP3/AAC.

The libraries are not stored in git. The first build of `VitaSharp.Desktop` downloads them, checks the SHA-256 and
caches them here, in one folder per platform (`win-x64/`, `linux-x64/`, ...). They are then copied to `ffmpeg/`
next to the executable. The logic and the pinned version live in [`build/FFmpeg.targets`](../../build/FFmpeg.targets).

| Platform | Source |
| --- | --- |
| Windows x64 / ARM64 | [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds), `win64` / `winarm64` LGPL shared |
| Linux x64 / ARM64 | [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds), `linux64` / `linuxarm64` LGPL shared |
| macOS | System FFmpeg 8 from Homebrew: `brew install ffmpeg` |

Current version: FFmpeg n8.1.3, release `autobuild-2026-09-30-13-08`. Its major version (libavcodec 62) must match
the `FFmpeg.AutoGen` package referenced by `VitaSharp.Core`.

Build without FFmpeg (offline, CI): `dotnet build -p:VitaSharpSkipFFmpeg=true`. Videos and MP3/AAC audio are then
disabled; everything else works.

FFmpeg is licensed under the LGPL 2.1 or later ([LICENSE-FFmpeg-LGPL.txt](LICENSE-FFmpeg-LGPL.txt)); the license
file of the downloaded build is copied next to the libraries. Source code: https://ffmpeg.org/.
