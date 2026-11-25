# PlayStation streaming engine

FIFINE Capture Studio carries the Remote Play engine with the app: the installer places
[Nixani Engine](https://github.com/Nixani/nixani-engine) 1.2.0 (protocol v3) under
`<app folder>\streaming\`. FIFINE starts it as a separate, window-less process and talks to it over
its documented stdin/stdout protocol, so the AGPL-3.0 engine stays a separate program.

The bundled folder contains `nixani-engine.exe`, its DLLs, `LICENSE.txt`, `LICENSE-AGPL-3.0.txt` and
`THIRD-PARTY-NOTICES.txt`. The corresponding source is the upstream tag
[v1.2.0](https://github.com/Nixani/nixani-engine/tree/v1.2.0).

## Engine lookup order

FIFINE uses the first existing path:

* **Gaming Sources → PlayStation Remote Play → Remote Play engine path**, if set
* `FIFINE_NIXANI_ENGINE` environment variable
* `<app folder>\streaming\nixani-engine.exe` (the bundled copy)
* `<app folder>\nixani-engine.exe`
* `<app-data>\streaming\nixani-engine.exe`
* `nixani-engine.exe` on `PATH`

An override is only needed when testing a different engine build. The engine needs its DLLs next to
the executable; keep the whole folder together.

## Build it yourself (optional)

Prerequisites (from the engine's README): git, Python 3, Visual Studio 2022 Build Tools with the C++
and "C++ Clang tools" components, plus vcpkg.

```powershell
git clone --recursive https://github.com/Nixani/nixani-engine
cd nixani-engine
.\setup.ps1
.\build.ps1
.\package.ps1
```

Copy the contents of `dist\nixani-engine\` over `tools\streaming\nixani-engine\` when updating the
bundled engine. `nixani-engine.exe --version` must report the protocol version this client speaks
(protocol 3 for engine 1.2.0).

## Use it

1. Enable Remote Play on the console (PS5: Settings → System → Remote Play; PS4: Settings → Remote
   Play).
2. In FIFINE: Gaming Sources → **Find & pair console**.
3. On the console choose **Link Device** (PS5) or **Add Device** (PS4); the TV shows 8 digits.
4. Enter the digits in FIFINE and press Connect. FIFINE discovers the console, registers, starts the
   session, and opens Live Capture once real frames arrive.
5. For a PS5, use **Sign in to PSN** once under Gaming Sources → PlayStation Remote Play (or paste
   a PSN account ID). Sony requires the account ID during link-code registration; the pairing sheet
   itself only asks for the 8-digit code.

## Tests without a console

`tests/NixaniEngineMock` implements the same protocol and streams a real H.264 asset. The integration
tests start it, register, stream, decode through FFmpeg and verify the pipeline — no console and no
engine build required:

```powershell
dotnet test tests/FifineCaptureStudio.Tests -c Release --filter "FullyQualifiedName~NixaniEngine"
```
