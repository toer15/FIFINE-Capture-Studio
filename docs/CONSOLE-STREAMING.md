# Console streaming backend

This document describes how FIFINE Capture Studio receives real console video, what is implemented,
and what has and has not been verified on hardware.

## PlayStation (implemented)

```
PS5 / PS4  ──Remote Play──▶  nixani-engine.exe  ──stdin/stdout protocol v3──▶  FIFINE
                                   │                                            │
                                   └── H.264/HEVC access units, PCM audio ──────┤
                                                                                ▼
                                                       named pipe ─▶ FFmpeg ─▶ preview (MJPEG) + recording
```

* **Engine process** — `Core/Streaming/Nixani/NixaniEngineClient.cs` starts the engine with
  redirected stdin/stdout (no window), performs the v3 handshake, discovery, registration
  (`RegistStart` with the 8-digit code), session start (`SessionStart` with resolution 360/540/720/1080,
  30/60 fps, bitrate, H.264/HEVC, DualSense flag), `Pad` controller updates, keyframe requests,
  session stop, and parses every engine message: `Hosts`, `RegistResult`, `RegistProgress`,
  `SessionState`, `Video`, `AudioFormat`, `Audio`, `Stats`, `SessionStopped`, `LoginPinRequest`,
  `CantDisplay`.
* **Discovery** — the engine's own mDNS/PSN discovery, not a port scan. Hosts are shown with their
  real state (on / rest mode) and system version.
* **Registration** — exactly the console flow: Settings → System → Remote Play → Link Device on PS5
  (PS4: Settings → Remote Play → Add Device), enter the 8 digits, FIFINE calls `RegistStart` and
  stores the returned registration keys DPAPI-protected. Nothing is stored from the code itself.
* **Video** — each `Video` message is one complete Annex B access unit. `VideoPipeFeed` publishes
  them through a Windows named pipe; `PipelineArgs` adds
  `-f h264|hevc -framerate <fps> -i \\.\pipe\<name>`, so the **existing** preview, fullscreen preview,
  replay buffer, recording and statistics pipeline runs unchanged. A new FFmpeg process (recording
  start/stop, quality change) reconnects to the same pipe and a keyframe is requested.
* **Audio** — `Audio` messages carry interleaved 16-bit PCM; `AudioEngine.PushConsolePcm` mixes it
  into the same mixer as game/capture/mic audio, so monitoring and recording (including separate
  tracks) include console sound.
* **Controller** — while streaming, the local controller (XInput or Sony HID) is mapped to the engine's
  `Pad` message: d-pad, face buttons, bumpers, sticks, triggers, share/options/PS. Forwarding stops
  when the session ends. Haptics/rumble from the console are received by the client but not yet played.
* **Statistics** — `Stats` messages (packets received/lost, RTT, FEC failures, keyframe requests) are
  shown as reported; nothing is invented. Frame rate and latency come from the decoder/preview, not
  from a timer.
* **Reconnection** — an unexpected `SessionState 3` or engine exit triggers up to three reconnects
  with backoff, and the pipeline re-attaches to the console stream.

### Requirements

The engine is a separate AGPL-3.0 program and is not bundled. Build it from
<https://github.com/Nixani/nixani-engine> (Visual Studio 2022 Build Tools + Clang, vcpkg, Python) or
download it from its project page, then set the path in **Gaming Sources → PlayStation Remote Play**.
FIFINE also looks in `<app>\streaming\nixani-engine.exe`, the app-data `streaming` folder, and `PATH`.

PS5 registration normally needs the PSN account ID (hex or base64) because the console ties the
client to the account; PS4 does not. The field is in the same card.

## Xbox (not implemented)

Xbox home streaming is delivered by Microsoft's xHome service over **WebRTC**, with MSA/XSTS tokens.
The two open implementations, Greenlight and xbox-xcloud-player (both MIT), are Node.js/Electron
programs whose video is rendered inside Chromium; there is no drop-in native library.

This build therefore **does not** ship an Xbox video backend and does not pretend to: the Xbox
pairing path still offers HDMI capture, and the UI states that no network video transport exists yet.
Implementing it would require embedding a WebRTC stack plus the xHome signalling flow and a real
console for verification.

## Verification performed

| Check | Result |
| --- | --- |
| Protocol framing, handshake, discovery, registration (accept + reject), session start/stop | Automated test against a mock engine process (`tests/NixaniEngineMock`) speaking v3 over real stdin/stdout |
| Video reception | 180 access units streamed by the mock; client counted frames, stats and audio packets |
| Decode through the named pipe | Real H.264 asset pushed through `VideoPipeFeed`; FFmpeg decoded it into an MP4 > 10 KB |
| DPAPI credential storage | Round-trip test; wrong-user/corrupt blob returns null |
| Link-code and PSN account-id parsing | Unit tests |
| UI | All ten views smoke-tested; PlayStation card added to Gaming Sources |

**Not verified:** a physical PS5/PS4 has never been connected here (no console is attached to this
machine), and `nixani-engine.exe` itself is not built in this environment. Protocol compliance is
verified against the published specification with a mock engine, not against the real engine and
console. Do not describe PlayStation streaming as "hardware verified" until a console delivers real
frames.
