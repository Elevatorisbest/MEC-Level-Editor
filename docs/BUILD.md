# Building and running

## Automatic build (GitHub Actions), no local Visual Studio needed

Every push to `dev` that changes code runs `.github/workflows/build.yml` on a GitHub Windows
Server 2022 runner (Visual Studio 2022). You can also start it by hand: GitHub → Actions → Build →
"Run workflow".

The run produces two artifacts (downloadable from the run's page, bottom section "Artifacts"):

| Artifact | Contents | Kept for |
|---|---|---|
| `FrostyEditor-<commit>` | Runnable editor (`Release - Final`, x64), all plugins that built, MEC type SDK only | 5 days |
| `build-logs-<commit>` | One MSBuild log per project | 7 days |

The run summary page shows a table of which plugins built. `LevelEditorPlugin` must build or the
run fails. The other plugins are best-effort.

Notes:
- `FrostyHash` (C++/CLI) asks for the VS 2017 toolset v141. The workflow uses v141 if the runner
  has it, otherwise v143, and prints which one it used in the "Report toolchain" step.
- `Shaders/FrostyShaders.vcxproj` is not built. The editor uses the precompiled `.bin` shaders
  that are already in the repo.
- Private repos have limited Actions minutes and artifact storage on the free plan, and Windows
  minutes count double. Artifacts expire quickly to stay inside the storage quota.

## Running a build on your PC

1. Download `FrostyEditor-<commit>.zip` from the run and unpack it **anywhere on D:**, for example
   `D:\FrostyBuilds\<commit>\`. Nothing needs to go on C:.
2. Requirements: .NET Framework 4.8 (already installed) and the Microsoft Visual C++ 2015–2022 x64
   redistributable (needed by FrostyHash; usually already installed by games).
   CI builds `Release - Final` on purpose. A `Developer - Debug` build needs Visual Studio's debug
   C++ runtime and has the crash handler compiled out, so on a PC without VS it exits silently.
3. Start `FrostyEditor.exe`, choose your Mirror's Edge Catalyst install as usual.
4. `BUILD_COMMIT.txt` in the folder says which commit you are running. Include it in test reports.

## Building locally (optional, not required)

Visual Studio 2022 with ".NET desktop development" and "Desktop development with C++" (plus
"C++/CLI support"). Open `FrostyLevelEditor.sln`, select `Developer - Debug` / `x64`, and build
projects individually (FrostyEditor first, then the plugins), not the whole solution.
