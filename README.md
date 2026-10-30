# NDI Browser Utility

A simple wrapper around [CEFSharp](https://github.com/cefsharp/CefSharp) and [NDI Wrapper for .NET](https://github.com/eliaspuurunen/NdiLibDotNetCoreBase). Sends HTML pages to NDI, including audio if playback on the page is supported.

[Grab the latest ZIP file](https://github.com/VisualBoy/NDI-Browser/releases) from the releases page.

<img width="1456" height="720" alt="ndi-browser-github-cover" src="https://github.com/user-attachments/assets/f848a81d-acff-4efa-a021-4e633f88db3d" />

### v2026.10.29: Integrated Dark Mode Navigation Bar & Custom Window Frame

<img width="1314" height="848" alt="image" src="https://github.com/user-attachments/assets/ba24c57e-800e-49f8-b9af-acd8946a5f25" />


## Prerequisites

- No external NDI Runtime installation is required for typical deployments—the ZIP now ships with `Processing.NDI.Lib.x64.dll` under `runtimes/win-x64/native/` and the app prefers that bundled copy automatically.
- If you prefer to use an existing runtime (or need to update the DLL independently), install the NDI Runtime (NDI® 6 or later). The app still scans common locations such as `C:\Program Files\NDI\NDI 6 Runtime\v6` and `...\NDI 6 SDK\Bin\x64`, and it honours the `NDILIB_REDIST_FOLDER` environment variable.

## Building from source

See [`Docs/building.md`](Docs/building.md) for detailed instructions. The
project targets **.NET 8.0 (windows)** and requires the Windows Desktop
reference packs, so make sure the .NET SDK you install includes the "Windows
desktop" workload before compiling.

### Build command:

`dotnet publish Tractus.HtmlToNdi.csproj -c Release -r win-x64 --self-contained false -o C:\VJ\NDI-Browser`

## Usage

Launching the executable without command-line parameters now opens a simple launcher window. The launcher loads the most recent settings, lets you tweak NDI, HTTP and rendering options, and starts the application when you press **Launch**. Settings are written to `launcher-settings.json` beside the executable and reused next time you open the tool.

If the web page you are loading has a transparent background, NDI will honor that transparency.

See [`Docs/configuration-recipes.md`](Docs/configuration-recipes.md) for ready-made flag combinations tailored to low-latency,
broadcast-smooth, deep-buffer, and compositor-driven scenarios.

## Command Line Parameters

Parameter|Description
----|---
`--ndiname="NDI Source Name"`|The source name this browser instance will send. Defaults to "`HTML5`".
`--w=1920`|The width of the browser source. Defaults to `1920`.
`--h=1080`|The height of the browser source. Defaults to `1080`.
`--port=9999`|The port the HTTP server will listen on. Defaults to `9999`.
`--url="https://www.tractus.ca"`|The startup webpage. Defaults to `https://testpattern.tractusevents.com/`.
`--fps=59.94`|Target NDI frame rate. Accepts integer, decimal or rational values (e.g. `60000/1001`). Defaults to `60`.
`--buffer-depth=3`|Enable the paced output buffer with the specified frame capacity. When enabled the sender waits for the queue to hold `depth` frames before transmitting, adding roughly `depth / fps` seconds of intentional latency. Set to `0` (default) to run zero-copy.
`--enable-output-buffer`|Shortcut to turn on paced buffering with the default depth of 3 frames (≈`3 / fps` seconds of latency once primed).
`--allow-latency-expansion`|Let the paced buffer keep playing any queued frames during recovery instead of immediately repeating the last frame. This trades temporary extra latency for smoother motion after underruns.
`--disable-capture-alignment`|Turns off the paced sender’s capture timestamp alignment (enabled by default). Use `--align-with-capture-timestamps` to explicitly re-enable it for a specific run.
`--disable-cadence-telemetry`|Suppresses the capture/output cadence jitter metrics in telemetry logs (enabled by default). Use `--enable-cadence-telemetry` to force-enable them when needed.
`--enable-paced-invalidation` / `--disable-paced-invalidation`|Ties Chromium invalidation to the paced sender so no more than one capture runs per send slot, even when the paced buffer is disabled. Defaults to disabled.
`--enable-capture-backpressure` / `--disable-capture-backpressure`|Pauses Chromium invalidation while the paced buffer is above its high-water mark, resuming automatically once depth settles. Requires `--enable-paced-invalidation`; when pacing is off the backpressure toggle is ignored. Defaults to disabled.
`--enable-pump-cadence-adaptation` / `--disable-pump-cadence-adaptation`|Allows the invalidation scheduler to stretch or delay Chromium renders using capture/output drift telemetry. Defaults to disabled.
`--enable-compositor-capture` / `--disable-compositor-capture`|Bypass the legacy invalidation loop and stream frames directly from Chromium's compositor via the native capture helper. Defaults to disabled.
`--telemetry-interval=10`|Seconds between video pipeline telemetry log entries. Defaults to 10 seconds.
`--windowless-frame-rate=60`|Overrides CEF's internal repaint cadence. Defaults to the nearest integer of `--fps`.
`--disable-gpu-vsync`|Disables Chromium's GPU vsync throttling. Defaults to off (VSync enabled).
`--disable-frame-rate-limit`|Disables Chromium's frame rate limiter. Defaults to off (Rate limit enabled).
`--enable-gpu-rasterization`|Forces Chromium to rasterize content on the GPU even for off-screen contexts. Enabled by default.
`--enable-zero-copy`|Enables zero-copy raster uploads from GPU memory to reduce texture copies. Enabled by default.
`--enable-oop-rasterization`|Moves rasterization work to the out-of-process raster thread for smoother rendering. Enabled by default.
`--disable-background-throttling`|Prevents Chromium from throttling timers or renderers when the view is hidden/off-screen. Enabled by default.
`--force-high-performance-gpu`|Forces Chromium to select the discrete high-performance GPU adapter.
`--enable-begin-frame-scheduling`|Enables Chromium's begin-frame scheduling for strict frame cadence control.
`--disable-new-content-rendering-delay`|Instructs Chromium to remove the rendering delay for newly added content.
`--preset-high-performance`|Preset combining GPU rasterization, zero-copy, OOP rasterization, disabled GPU vsync/frame rate limit, and disabled background throttling.
`--cef-extra-args="k1=v1;k2=v2"`|Passes semicolon-separated switches directly to CEF/Chromium. Setting a switch to `=0` removes it from active arguments.
`--preview` / `--enable-preview`|Displays an interactive desktop preview window that renders the live OSR stream and translates/forwards mouse and keyboard input directly into Chromium while streaming to NDI. Enabled by default in the launcher.
`--disable-preview`|Suppresses the interactive preview window when launching via CLI.
`--launcher`|Forces the launcher window to appear even when other parameters are supplied.
`--no-launcher`|Skips the launcher and honours the supplied command-line arguments only.

> **Hardware acceleration & WebGL:** GPU rasterization, zero-copy uploads, out-of-process rasterization, WebGL, 2D canvas acceleration, hardware video decode, and unthrottled frame rates are now active by default for maximum throughput.

> **Webcam & microphone sharing:** WebRTC media access requests (camera, microphone) are automatically granted using system default devices with no blocking permission prompts.

> **Interactive desktop preview:** The interactive preview window runs on an independent UI thread and mirrors the off-screen render buffer in real time. Full mouse (moves, clicks, wheel) and keyboard events are translated and forwarded into Chromium. Closing the preview window does not interrupt or terminate the live NDI broadcast.

> **Experimental parallel path:** `--enable-compositor-capture` (and the matching launcher checkbox) opt into an alternative capture pipeline that relies on Chromium's compositor rather than UI-thread invalidations. It is intentionally isolated behind this flag; the legacy path remains the default and continues to run unchanged when the flag is left off. Treat this mode as preview-only while we evaluate performance and stability—do not enable it for shows unless you are actively testing it.

When the paced buffer is enabled the pipeline repeats the most recently transmitted frame while warming up or recovering from an underrun so receivers continue to see a stable cadence. Passing `--allow-latency-expansion` switches that recovery into a variable-latency mode that keeps playing any queued frames before falling back to repeats, smoothing out motion at the cost of temporary additional delay. The launcher exposes checkboxes for latency expansion, paced invalidation, a force-disable option for paced invalidation, capture backpressure, pump cadence adaptation, capture alignment, and cadence telemetry so operators can toggle those behaviours without touching the command line. See [`Docs/paced-output-buffer.md`](Docs/paced-output-buffer.md) for a deeper walkthrough of the priming and telemetry behaviour.

### Pacing, invalidation, and backpressure

Chromium renders are now driven by a pacing-aware scheduler that coordinates invalidations with the paced output buffer. When `--enable-paced-invalidation` is set the scheduler runs Chromium in on-demand mode so each send slot triggers at most one capture. The same scheduler feeds cadence adaptation (when `--enable-pump-cadence-adaptation` is active), allowing Chromium to stretch or delay invalidations slightly so capture stays aligned with the paced sender. Capture backpressure (`--enable-capture-backpressure`) piggybacks on this scheduler: the capture gate pauses invalidations while the buffer sits above its high-water mark and resumes them automatically once depth settles. Because backpressure depends on paced invalidation, the pipeline ignores the toggle (and the launcher clears the checkbox) whenever pacing is off.

The invalidation scheduler tracks outstanding requests with short-lived tickets so backpressure, direct pacing, and buffered pacing all share a single pending-slot counter. Tickets are automatically discarded once the corresponding paint arrives, a request is cancelled, or the timeout elapses. During Chromium stalls the timeout handler now trims expired tickets before scheduling follow-up invalidations, preventing the queue from growing unbounded while capture is paused. Direct paced sends also run a watchdog that periodically tops up pending requests so Chromium keeps rendering even when recent sends have been paused, making the ticket queue resilient to slow receivers. When the paced pipeline resets, the ticket registry is drained under lock and finalized outside the critical section so stale entries cannot accumulate during recovery.

When the compositor capture helper is active (`--enable-compositor-capture`) the pipeline bypasses the frame pump entirely and lets Chromium's compositor drive delivery cadence. Telemetry entries now append `compositorCapture`, `compositorFrames`, and `legacyInvalidationFrames` so operators can compare throughput and capture quality between the compositor and invalidation-driven paths without switching logging formats.

Telemetry reflects the pacing state. Buffer health logs now include `captureGateActive`, `captureGatePauses`, and `captureGateResumes` so operators can see when backpressure engaged. Additional fields such as `pacedPaused`, `pacedOffsetMs`, and `cadenceAdaptation` describe how the scheduler is steering Chromium, while `resyncDrops` shows when stale frames were trimmed to get latency back under control.

#### Example Launch

`.\Tractus.HtmlToNdi.exe --ndiname="HTML 5 Test" --w=1080 --h=1080 --url="https://testpattern.tractusevents.com"`

## API Routes

Route|Method|Description|Example
----|----|----|---
`/seturl`|`POST`|Sets the URL for this instance.|`{"url": "https://www.google.ca"}`
`/scroll/{increment}`|`GET`|Scrolls the page vertically.|`/scroll/-100` (scrolls up)
`/click/{x}/{y}`|`GET`|Simulates a left mouse click at the specified coordinates.|`/click/100/200`
`/keystroke`|`POST`|Sends a sequence of keystrokes.|`{"toSend": "Hello, world!"}`
`/type/{toType}`|`GET`|A convenience endpoint for sending keystrokes via a GET request.|`/type/Hello%2C%20world%21`
`/refresh`|`GET`|Refreshes the current page.|`/refresh`

## Known Limitations

- Frames are sent to NDI in RGBA format. Some machines may experience a slight performance penalty.
- H.264 and any other non-free codecs are not available for video playback since this uses Chromium. Sites like YouTube likely won't work.
- Audio data received from the browser is passed to NDI directly.
- NDI frame rate defaults to 60 fps but can be overridden with `--fps`. Chromium's internal repaint cadence can be adjusted with `--windowless-frame-rate`.


