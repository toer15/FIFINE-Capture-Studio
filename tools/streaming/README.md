# PlayStation streaming engine

FIFINE Capture Studio does not ship the Remote Play engine. It talks to
[Nixani Engine](https://github.com/Nixani/nixani-engine) as a separate process over its documented
stdin/stdout protocol, so the engine keeps its own AGPL-3.0 licence and this repository stays free of
its code.

## Build it

Prerequisites (from the engine's README): git, Python 3, Visual Studio 2022 Build Tools with the C++
and "C++ Clang tools" components, plus vcpkg.

```powershell
git clone --recursive https://github.com/Nixani/nixani-engine
cd nixani-engine
.\setup.ps1
.\build.ps1
.\package.ps1
```

The packaged build contains `nixani-engine.exe` (and its DLLs). Put the whole folder anywhere and
tell FIFINE where the executable is:

* **Gaming Sources → PlayStation Remote Play → Remote Play engine path**, or
* `<app folder>\streaming\nixani-engine.exe`, or
* anywhere on `PATH`, or
* the `FIFINE_NIXANI_ENGINE` environment variable.

The engine needs its DLLs next to the executable; point at the executable inside its own folder.

## Use it

1. Enable Remote Play on the console (PS5: Settings → System → Remote Play; PS4: Settings → Remote
   Play).
2. In FIFINE: Gaming Sources → **Find & pair console**.
3. On the console choose **Link Device** (PS5) or **Add Device** (PS4); the TV shows 8 digits.
4. Enter the digits in FIFINE and press Connect. FIFINE registers, starts the session, and opens Live
   Capture once real frames arrive.
5. For PS5, fill in the PSN account ID (hex or base64) before registering if the engine asks for it.

## Tests without a console

`tests/NixaniEngineMock` implements the same protocol and streams a real H.264 asset. The integration
tests start it, register, stream, decode through FFmpeg and verify the pipeline — no console and no
engine build required:

```powershell
dotnet test tests/FifineCaptureStudio.Tests -c Release --filter "FullyQualifiedName~NixaniEngine"
```
