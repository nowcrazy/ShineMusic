# Local FFmpeg for media tests

Real media tests accept a directory containing both `ffmpeg` and `ffprobe`, in this order:

1. `SHINEMUSIC_TEST_FFMPEG_BIN`.
2. This directory's `bin/` folder in a repository checkout.
3. A directory on the process `PATH`.

On Windows the executable names end in `.exe`. The test helper supplies absolute paths to both the synthetic-audio generator and `FfmpegMediaService`; it does not change the system `PATH`.

The local Windows tools prepared on 2026-11-25 are Gyan's FFmpeg 9.1 essentials build. [FFmpeg's download page](https://ffmpeg.org/download.html#build-windows) lists Gyan as a Windows build provider. The source archive was downloaded from [Gyan's release essentials ZIP](https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip), verified against its [published SHA-256](https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip.sha256), and only the two executables plus the included license were extracted here.

Verified archive SHA-256:

```text
B9BBBB3ACECE000CBEEC994F856649354E52C773807841E78908374B24D658FD
```

The release URL can change when the provider publishes another version, so verify the checksum for a fresh download. Binaries and archives are local ignored dependencies. The extracted license is available locally as `LICENSE.txt`.

To choose an existing installation for one PowerShell session:

```powershell
$env:SHINEMUSIC_TEST_FFMPEG_BIN = 'C:\path\to\ffmpeg\bin'
dotnet test ShineMusicApi/ShineMusic.Tests/ShineMusic.Tests.csproj
```
