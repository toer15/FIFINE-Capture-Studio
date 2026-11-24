# Gaming Enhancement

FIFINE Capture Studio includes a real-time GPU image-enhancement pipeline for gameplay coming from
the PlayStation Remote Play backend, a capture card, a display or a window. It processes the incoming
video only — it never needs access to a game's internal render targets.

![Gaming Enhancement page](enhancement-page.png)

## What actually runs

```
capture pipeline ──MJPEG preview (native size when enhanced)──► CPU JPEG decode ──BGRA──► newest-frame slot
                                                                                        │
                          Direct3D 11 compute ◄──────── one upload per displayed frame ─┘
                          ├─ edge-aware denoise + deblock
                          ├─ detail / edge / texture reconstruction
                          ├─ colour grading (SDR)
                          └─ Catmull-Rom super resolution + adaptive sharpen
                          ──one readback──► preview / split / side-by-side (WPF compositor)
```

Every frame keeps its decode-completion timestamp and sequence number through the slot, the GPU
passes and the presentation notification, so the monitoring page can report where the time went.

* Implemented in `Core/Enhancement/` with Direct3D 11 compute shaders (HLSL, `cs_5_0`) through
  SharpDX. Every pass is a real GPU dispatch; source frames stay resident on the GPU across the whole
  chain, with exactly one upload and one readback per displayed frame. There is no GPU texture export
  yet, so the enhanced image is copied back to CPU memory to be shown by WPF.
* Shaders: `CSDenoise`, `CSGrade`, `CSUpscale`, `CSCopy` in `EnhancementShaders.cs`. No "AI" model is
  bundled or downloaded; the enhancement is a classical, deterministic GPU pipeline. That is stated
  plainly in the UI rather than being sold as a neural network.
* Super resolution targets: native, 1080p, 1440p and 4K. 6K/8K are not offered. The page always shows
  the original source resolution next to the enhanced output resolution and states that reconstructed
  output does not contain native detail the source never had.
* Colour: brightness, contrast, saturation, vibrance, gamma, temperature, tint/white balance, black
  level, shadow detail and highlight recovery. Input is handled as SDR; HDR is never invented.
* Profiles (Natural, Ultra Sharp, Cinematic, Vibrant, Clean Image, Custom) write concrete slider
  values, so nothing is hidden behind a preset.
* Comparison: single view, split (enhanced right half over the original) and side-by-side; holding
  **Tab** in Live Capture shows the untouched signal, releasing restores the enhanced view. The same
  hold-to-compare and quick toggle are available in the fullscreen gameplay preview.
* A panic control ("Disable all enhancement") switches processing off immediately.

## Preview resolution and the transport

The lightweight preview remains 1600 px wide. When Gaming Enhancement is enabled for the preview, the
capture pipeline now requests the **native source width** instead (capped at 3840 px so an 8K desktop
capture cannot turn the MJPEG transport into an unbounded stream). The resolution strip on the page
shows capture source, enhancement input and enhanced output separately, because they are three
different numbers.

| what | when |
| --- | --- |
| 1600 px preview | enhancement off, or full-resolution input disabled |
| native source width, ≤ 3840 | enhancement on and full-resolution input enabled (default) |
| native source width, called out as reduced | automatic fallback after sustained decode overload |

![Native-resolution enhancement input beside the capture source size](enhancement-native-input.png)

The transport is still FFmpeg → MJPEG → CPU JPEG decode. There is no D3D11VA decode of these frames
to tap into: the frames arrive as JPEG over a pipe, not as a hardware-decoded H.264/HEVC surface. A
true GPU-native path requires replacing the subprocess transport with in-process decode (or a
shared-handle decoder) and is not claimed here. The UI therefore says which route is active instead
of implying shared textures.

Because CPU JPEG decoding becomes the bottleneck of that transport at high sizes, the session watches
the measured decode time whenever the enhancement preview runs above 1600 px. If the moving average
stays above roughly 45% of the frame budget for 90 frames, it persists `FullResolutionPreview = false`,
shows a notice and restarts the preview at 1600 px. Recording and the encoded output are untouched by
this fallback: they always keep the full source resolution.

## Measured on the target hardware

AMD Radeon RX 9070 XT, driver 32.0.32029.2010. Headless benchmark on synthetic frames sized like the
real input, target equal to the source (Native super resolution), 120 frames, 2026-11-24:

| input → output | average | p50 | p95 | upload (CPU→GPU) | readback | VRAM |
| --- | --- | --- | --- | --- | --- | --- |
| 1920 × 1080 → 1920 × 1080 | 2.67 ms | 2.36 ms | 3.91 ms | 0.80 ms | 0.09 ms | 47 MB |
| 2560 × 1440 → 2560 × 1440 | 4.71 ms | 4.14 ms | 6.94 ms | 1.45 ms | 0.21 ms | 73 MB |
| 3840 × 2160 → 3840 × 2160 | 13.10 ms | 10.86 ms | 18.27 ms | 6.01 ms | 0.53 ms | 141 MB |

At 1440p and below the GPU ceiling is far above 60 fps. At 4K the average is 13.1 ms (≈76 fps), so a
4K60 enhancement preview is possible on this GPU, but the p95 of 18.3 ms leaves very little headroom and
the CPU upload is already 6 ms of it. This is why native input is a toggle with an automatic fallback
rather than an unconditional default.

Earlier measurement with the 1600 × 900 transport at a 1440p target: **3.8 ms average, p50 3.5 ms,
p95 4.8 ms**, and a live 1080p60 capture processed at **60.1 fps with 105 MB VRAM**. Those numbers
remain valid for the lightweight preview path.

The benchmark checks the result by reading back the frame and requiring real colour in the majority of
sampled pixels, not merely a non-empty buffer. That check is what caught an earlier D3D11 binding bug
where every pass wrote black; it now fails loudly if the pipeline stops producing image data.

Reproduce the headless benchmark (no UI, real GPU work, writes `%LOCALAPPDATA%\FifineCaptureStudioData\enhancement-bench.txt`):

```
FifineCaptureStudio.exe --enhance-bench 180
FifineCaptureStudio.exe --enhance-bench 120 --target 2160
FifineCaptureStudio.exe --enhance-bench 60 --image C:\path\frame.png --target 1440
```

`--image` runs a real decoded frame through the same engine and saves the processed result to
`enhance-bench-output.png` next to the report, which is the quickest way to eyeball quality changes.

![Live Capture with the compact enhancement panel](enhancement-quick-panel.png)

The compact panel in Live Capture exposes sharpening, contrast and vibrance over gameplay, plus a link
to the full page and a single "Disable all enhancement" button.

## Freshness queue and latency telemetry

Enhanced frames are handed to the worker through a one-slot queue. A new frame **replaces** the queued
one when processing falls behind, instead of waiting behind stale work, so the pipeline can never build
seconds of backlog. Replaced frames are counted as dropped, and the processed frame keeps its original
sequence number, decode timestamp and age.

The page reports, with the measurement boundary stated in the UI:

* **Source / enhancement input / enhanced output** resolution.
* **Source FPS** (capture session), **processed FPS** and **presented FPS** as separate windows.
* **Dropped** frames (replaced queued frames) and **queue age** at the moment processing begins.
* **Decode** time, **GPU shader** time (sum of timestamp-query passes) and **frame total** (GPU + CPU
  readback wait + upload). Shader time is never labelled as total latency.
* **Capture → present** from decode completion to the compositor pass that paints the enhanced frame,
  with average and p95 over the last 240 samples. This is an in-app measurement: camera exposure,
  capture-card buffering and display scan-out are outside it, and it is not described as glass-to-glass.

![Frame-rate, queue and latency telemetry](enhancement-telemetry.png)

The earlier build measured only the GPU staging copy as `ReadbackMs`. `Map` blocks until the copy has
finished, so that wall-clock wait is now measured separately (`ReadbackWaitMs`) and included in the
frame total, while the GPU copy keeps its own timestamp-query value. The enhanced worker now also raises
the presentation notification; previously only the plain preview path did, so enhanced latency could
never be sampled.

## Recording differences from the live preview

Enhanced recordings inject a real Vulkan/libplacebo filter chain into the existing encoder process
(`libplacebo=w=…:h=…:upscaler=lanczos:deband=…:brightness=…:contrast=…:saturation=…:gamma=…,cas=…`),
so scaling, colour grading and debanding happen on the GPU while the encoder, audio pipes, replay buffer
and file handling stay untouched. Generated frames are never written to a recording.

Live preview and recording are **not** the same implementation, and the UI does not claim they are:

| setting | live compute pipeline | recording filter |
| --- | --- | --- |
| brightness / contrast / saturation+vibrance / gamma | yes | yes (`libplacebo`) |
| debanding / denoise | edge-aware compute passes | `deband` only |
| sharpening | adaptive sharpen pass | `cas` |
| detail recovery, texture, edge clarity, anti-aliasing | yes | not expressible in this filter chain |
| temperature, tint/white balance, black level, shadows, highlights | yes | not applied (would need a LUT/curve stage) |
| output size | preview target | full recording resolution |

Recording always reads the full-resolution source through the existing encoder path, keeps the real
audio pipe and replay behaviour, and never receives generated frames. Aligning the remaining colour
stages would require a matching LUT chain in the recording filter; until that is validated visually,
the difference is documented rather than glossed over.

## Frame generation — not enabled

No 2X/3X/4X setting is offered, because the project does not yet have a frame-generation pipeline
that could honestly display what it generates:

* **AMD FSR / AFMF** operate inside a game's own swapchain through the driver and cannot process an
  external console or capture-card video stream.
* **FFmpeg `minterpolate`** is CPU motion interpolation and cannot sustain 1080p60 in real time; the
  user brief explicitly excludes shipping it as "AI frame generation" (the older recording-only
  auto-interpolation setting is separate and is not presented as this engine).
* A genuine implementation needs a GPU optical-flow estimator, bidirectional warping with occlusion
  handling, scene-cut detection and, critically, a **high-refresh Direct3D swapchain presenter** —
  WPF's compositor cannot present 120–240 generated frames per second, so generating them into the
  existing preview would show fabricated numbers rather than real output.

The FRAME GENERATION card in the app states this; it contains no decorative controls and no invented
FPS. The measured headroom above is the budget such a pipeline would have.

A future generator also implies a latency trade-off that must be measured, not hand-waved: temporal
interpolation needs the next source frame before it can synthesise an in-between frame, so enabling it
adds at least one source-frame interval of lookahead. For a 60 fps source that is ≈16.7 ms before the
presenter draws anything. The pipeline and UI keep source FPS, generated FPS and display FPS as three
separate numbers precisely so this cannot be hidden behind "120 FPS". Generating frames never makes a
60 Hz game render or respond like a native 120 Hz game.

The compute pipeline already exposes the seams a real implementation needs: per-frame sequence and
timestamp, a bounded frame slot, GPU-resident frames between passes, and a presentation notification.
What does not exist yet is the optical-flow/motion-compensation engine and a non-WPF high-refresh
presenter (DXGI flip-model swap chain); until both exist and are measured, the feature stays absent.

## Limits

* The enhanced preview runs on CPU-decoded MJPEG frames from the FFmpeg subprocess. Native resolution
  input is available, but GPU-native decode/texture sharing is not, so the decode and one CPU→GPU
  upload per displayed frame remain.
* The 4K native path is GPU-feasible (13.1 ms average here) but upload-bound; the automatic fallback
  protects 60 fps sources when MJPEG decode cannot hold the budget.
* Presentation still goes through the WPF compositor. A DXGI flip-model / DirectComposition presenter
  for high-refresh generated frames is designed for but not implemented.
* Enhancement adds GPU load and preview latency; Ultra Low Latency reduces or disables passes.
* HDR processing is not offered, because the capture paths do not expose reliable HDR metadata.
