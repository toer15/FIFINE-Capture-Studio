# Third-party notices

FIFINE Capture Studio is distributed under the terms in `LICENSE`. It uses the following
third-party software. The Nixani Remote Play engine is bundled as a separate program; the other
components below are installed by the user, and their own licences apply to them.

## Nixani Engine — AGPL-3.0 (bundled)

* Project: <https://github.com/Nixani/nixani-engine>
* Bundled version: 1.2.0, protocol v3
* Corresponding source: <https://github.com/Nixani/nixani-engine/tree/v1.2.0>
* Licence: GNU Affero General Public License v3.0 (`streaming\LICENSE-AGPL-3.0.txt`)
* Upstream: <https://github.com/chiaki-ng/chiaki-ng> (`libchiaki`, also AGPL-3.0)

FIFINE starts `nixani-engine.exe` as a **separate process** and communicates with it only through
its documented standard-input/standard-output message protocol. No engine code is copied into or
linked with FIFINE Capture Studio. The engine binary is redistributed unmodified under
`streaming\` in the installer, together with its own licence and third-party notice files:
`nixani-engine.exe`, its runtime DLLs, `LICENSE.txt`, `LICENSE-AGPL-3.0.txt` and
`THIRD-PARTY-NOTICES.txt`.

The complete corresponding source for the bundled engine is the upstream `v1.2.0` tag linked above.
If you redistribute FIFINE, keep the engine's licence files with it and preserve that source offer.
The engine's own third-party notice file lists every component and licence inside the engine build.

## Nixani Engine protocol description — MIT

The protocol interface (`PROTOCOL.md` and `protocol/nxe_protocol.h` in that repository) is MIT
licensed. `src/FifineCaptureStudio/Core/Streaming/Nixani/NixaniFrames.cs` is an independent C#
implementation written from that description; it contains no engine source.

## chiaki-ng — AGPL-3.0

<https://github.com/chiaki-ng/chiaki-ng> — the Remote Play implementation the engine is built on.
Not used directly by FIFINE.

## Greenlight and xbox-xcloud-player — MIT

* <https://github.com/unknownskl/greenlight> (MIT)
* <https://github.com/unknownskl/xbox-xcloud-player> (MIT)

These were evaluated for Xbox home streaming. They are Node.js/Electron applications whose video
arrives through WebRTC inside a browser engine; they are **not** embedded in this build and no Xbox
network video path is shipped. Their MIT terms are recorded here so any future integration starts
from a reviewed licence position.

## FFmpeg — GPL (user-installed)

FIFINE downloads the BtbN GPL build from <https://github.com/BtbN/FFmpeg-Builds> on request and
invokes it as a separate executable (`ffmpeg`, `ffprobe`). It is not redistributed in this repository.

## Microsoft Edge WebView2 — Microsoft licence

<https://learn.microsoft.com/microsoft-edge/webview2/> — hosts Sony's PlayStation Network sign-in
page inside the app. The WebView2 runtime is a Microsoft Edge component installed by Windows or the
user; the application references `Microsoft.Web.WebView2` and does not redistribute the runtime.

## Velopack — MIT

<https://github.com/velopack/velopack> — installer and update engine, referenced as a NuGet package.

## NAudio — MIT

<https://github.com/naudio/NAudio> — WASAPI capture/playback for the audio engine.

## Blacklight

Blacklight and its noncommercial licence terms are **not** used anywhere in this project, and no
code or binaries from it are included.

## SharpDX 4.2.0 (MIT)

The GPU enhancement engine uses SharpDX for Direct3D 11 interop. SharpDX is distributed under the
MIT licence (Copyright (c) 2010-2014 SharpDX SAS). No SharpDX source is modified or bundled as a
separate executable; the NuGet assemblies ship with the application.

## libplacebo (LGPL-2.1-or-later, via FFmpeg)

Enhanced recordings use FFmpeg's `libplacebo` filter (Vulkan). libplacebo itself is not bundled by
FIFINE: it is compiled into the FFmpeg build the user installs, and is only used when the user enables
enhanced recordings. See the FFmpeg build's own licence notes for the exact configuration.

## Vulkan runtime

`libplacebo` runs through the system Vulkan loader and the installed GPU driver (AMD Software:
Adrenalin). No Vulkan SDK is shipped with FIFINE.

## No AI model files

This project ships no neural-network weights. The enhancement pipeline is a deterministic compute
pipeline; no model is downloaded at runtime.
