# Third-party notices

FIFINE Capture Studio is distributed under the terms in `LICENSE`. It talks to the following
third-party software at runtime. **None of the components below are bundled, linked or modified by
this repository**; they are installed by the user, and their own licences apply to them.

## Nixani Engine — AGPL-3.0

* Project: <https://github.com/Nixani/nixani-engine>
* Licence: GNU Affero General Public License v3.0 (repository `LICENSE`)
* Upstream: <https://github.com/chiaki-ng/chiaki-ng> (`libchiaki`, also AGPL-3.0)

FIFINE starts `nixani-engine.exe` as a **separate process** and communicates with it only through
its documented standard-input/standard-output message protocol. No engine code is copied into,
linked with, or redistributed inside FIFINE Capture Studio. The engine is not part of the installer
and is not downloaded automatically: users who want PlayStation Remote Play build or obtain it
themselves and point FIFINE at the executable (Settings → PlayStation engine path).

If you redistribute FIFINE together with a build of Nixani Engine, you must comply with the AGPL for
that engine build — including offering its complete corresponding source — and keep this notice.

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

## Velopack — MIT

<https://github.com/velopack/velopack> — installer and update engine, referenced as a NuGet package.

## NAudio — MIT

<https://github.com/naudio/NAudio> — WASAPI capture/playback for the audio engine.

## Blacklight

Blacklight and its noncommercial licence terms are **not** used anywhere in this project, and no
code or binaries from it are included.
