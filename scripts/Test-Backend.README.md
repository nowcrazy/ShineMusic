# Isolated backend verification

Run this script with Windows PowerShell 5.1 or PowerShell 7 from a local checkout:

```powershell
./scripts/Test-Backend.ps1
```

For a Windows verification gate, invoke a separate process so `$LASTEXITCODE`
reliably reports both test failures and setup/cleanup errors. If the Release
build has already passed, replace the bare `dotnet test` gate with:

```powershell
powershell.exe -NoProfile -File .\scripts\Test-Backend.ps1 -SkipBuild -PostgresPort 45440
if ($LASTEXITCODE -ne 0) { $fail += 'Backend test gate failed.' }
```

The port must be free. The script configures its test database only for its own
run; a later bare `dotnet test` still requires an explicit test connection.

The script requires a running Docker daemon, a local `postgres:16-alpine` image,
the .NET SDK, and both FFmpeg tools. It does not pull container images. On Windows
it discovers Docker on `PATH`, in the standard Program Files installation, or in
the current user's `LocalAppData/Programs/DockerDesktop` installation. An explicit
CLI path can be supplied when discovery is unavailable:

```powershell
./scripts/Test-Backend.ps1 -DockerPath 'C:/path/to/docker.exe' -FfmpegBin 'C:/path/to/ffmpeg/bin'
```

Each run creates a uniquely named PostgreSQL container and a dedicated
`shinemusic_test_...` database, bound only to `127.0.0.1:45439`. Use `-PostgresPort`
if that port is occupied. The generated database password is passed through
process environment variables and is not printed or placed in CLI arguments.
Existing process values are restored when the script exits.

The default run builds the backend Release solution and executes all backend
tests. Use `-SkipBuild` only when a matching Release build is already available.
A uniquely named `backend-isolation-...trx` report is written under
`ShineMusicApi/.artifacts/test-results`; `-ResultsDirectory` can select another
directory, with relative paths resolved from the repository root.
Use `-TrxFileName backend-isolation-final.trx` when a fixed report name is needed.

Each test has a 120-second hang timeout. Use `-TestHangTimeoutSeconds` (30 to
3600) when a slower environment needs a different limit. A timeout ends the test
host and fails the run with a diagnostic test sequence; it does not create a
memory dump. This prevents a broken synchronization test from waiting forever.

If another local test process holds the usual output DLLs, set the .NET SDK
`ArtifactsPath` environment variable to an absolute, separate build directory
for this process before invoking the script. Both build and test use that
directory. Do not use `-SkipBuild` unless that directory already contains the
matching Release outputs.

Finally, the script checks its random ownership label before removing its own
container by ID, then verifies that the ID is no longer present. It never removes
a pre-existing container. A failed cleanup makes an otherwise successful run
fail; if tests already failed, the original error is retained with a cleanup
warning. Docker Desktop itself remains running.
