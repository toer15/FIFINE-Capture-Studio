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

The engine is a separate AGPL-3.0 program and is bundled with the app at
`<app>\streaming\nixani-engine.exe` (Nixani Engine 1.2.0, protocol v3). Its licence texts and source
URL are in that folder and in `THIRD-PARTY-NOTICES.md`. No build or download is needed. The
**Remote Play engine path** field is an optional override for testing another build; FIFINE also
looks at the app-data `streaming` folder and `PATH`.

PS5 registration needs the PSN account ID (hex or base64) because the console ties the client to
the account; PS4 does not. **Gaming Sources → Platform accounts** has a PlayStation row; **Sign in
with Sony** opens Sony's own page in a WebView2 window and displays the real **Online ID** returned
for the account. The account ID and OAuth tokens are stored (tokens DPAPI-protected per Windows
user), so a valid session is restored on the next launch and can be refreshed. Signing out clears
only the PlayStation account and its tokens; a manually pasted PSN account ID remains possible but
is not presented as a signed-in account. Without an account ID the PS5 rejects the link with
`CE-110032-7` (invalid PSN ID), so FIFINE stops before sending an all-zero ID and says what to do.
After sign-in, the pairing sheet only asks for the 8-digit code shown on the TV.

If the PS5 reports `0x80108b12`, Remote Play is disabled for the PSN user that is active on the
console (PS5 10.00+ has a per-user Remote Play setting), or the app is not using that user's PSN
account. Sign in here with the same PSN account that is active on the console, turn on
**Settings → System → Remote Play** for that user, then pair again with a fresh code. FIFINE now
requires a real, non-zero PSN account ID before it starts a PS5 session and reports this rejection
instead of the generic "Session timeout".

The Xbox row uses Microsoft's device-code flow, then Xbox Live user/XSTS authentication and the
profile service to show the actual Gamertag. Xbox Live requires a Microsoft Entra app registration
client ID (public client flows enabled); the row contains the setup steps and accepts that public
ID. The PC row is deliberately **Not available**: FIFINE Link Agent pairs a PC but has no account
identity to authenticate.

## Xbox (not implemented)

Xbox home streaming is delivered by Microsoft's xHome service over **WebRTC**, with MSA/XSTS tokens.
The two open implementations, Greenlight and xbox-xcloud-player (both MIT), are Node.js/Electron
programs whose video is rendered inside Chromium; there is no drop-in native library.

This build therefore **does not** ship an Xbox video backend and does not pretend to: the Xbox
pairing path still offers HDMI capture, and the UI states that no network video transport exists yet.
Implementing the video path would require embedding a WebRTC stack plus the xHome signalling flow and
a real console for verification. Xbox **account** sign-in is separate and is implemented with
Microsoft's device-code flow; it needs a Microsoft Entra app registration client ID supplied in the
Xbox account panel.

## Verification performed

| Check | Result |
| --- | --- |
| Protocol framing, handshake, discovery, registration (accept + reject), session start/stop | Automated test against a mock engine process (`tests/NixaniEngineMock`) speaking v3 over real stdin/stdout |
| Video reception | 180 access units streamed by the mock; client counted frames, stats and audio packets |
| Decode through the named pipe | Real H.264 asset pushed through `VideoPipeFeed`; FFmpeg decoded it into an MP4 > 10 KB |
| DPAPI credential storage | Round-trip test; wrong-user/corrupt blob returns null |
| Real engine handshake | Bundled `nixani-engine.exe` 1.2.0 reports protocol 3, starts and answers `Hello`; discovery found the PS5 on this network |
| PSN sign-in page | Sony's sign-in page loads in the app's WebView2 window. Token exchange and Online-ID retrieval require a real PSN account and were not completed here |
| Account state transitions | 25 mocked tests: pending authorization, successful sign-in, actual Gamertag/Online ID display, restore, expiry, profile failure, independent sign-out, and PC unavailable |
| Link-code and PSN account-id parsing | Unit tests |
| UI | All views smoke-tested, including the Platform accounts panel; PlayStation card added to Gaming Sources |

**Not verified:** a physical PS5/PS4 has never been connected here (no console is attached to this
machine). Protocol compliance is verified against the published specification, the mock engine and
the real engine's handshake, not against a console. Do not describe PlayStation streaming as
"hardware verified" until a console delivers real frames.
