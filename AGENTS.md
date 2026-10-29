# AGENTS.md — Project briefing for Tractus.HtmlToNdi

> **LLM Agent instruction:** Whenever you add new functionality or change existing behaviour, update this document to keep it accurate.

> Repository: [`tractusevents/Tractus.HtmlToNdi`](https://github.com/tractusevents/Tractus.HtmlToNdi)
> Purpose: Windows-only utility that renders a Chromium page off-screen and publishes video/audio plus limited KVM over the NDI
protocol, with a minimal HTTP control API.

This document is the ground-truth orientation guide. Treat it as a living spec—update it whenever behaviour changes.

For the current paced-buffer design direction and recommendations, review `Docs/paced-buffer-improvement-plan.md` alongside the
historical evaluation in `Docs/paced-buffer-pr-evaluation.md`.

For a concrete 30p stability recipe (CLI switches plus launcher checkboxes), see `Docs/cadence-stability.md`.

---

## 1. What the current build actually does (net8.0, early 2025 snapshot)

* **Entry point:** `Program.Main` wires AppDomain / TaskScheduler / WinForms exception handlers, strips launcher-only flags, and
  initializes Serilog via `AppManagement.Initialize`. If `--launcher` (or a persisted launcher preference) is present it shows
  `LauncherForm`, persisting settings through `LauncherSettingsStore`; otherwise it parses CLI flags with
  `LaunchParameters.TryFromArgs`. Successful parsing flows into `RunApplication` with sanitized arguments and a fresh Cef cache
  directory under `cache/<guid>`.
* **Chromium lifecycle:** `RunApplication` ensures the native NDI runtime is available (first trying the packaged
  `runtimes/win-<arch>/native/Processing.NDI.Lib.*` before probing the machine) and creates the unmanaged sender before touching
  Chromium. `AsyncContext.Run` plus `SingleThreadSynchronizationContext` keep CefSharp on a dedicated STA-like thread. After the
  first page load `CefWrapper.InitializeWrapperAsync` unmutes audio, applies the requested windowless frame rate, and either:
  * enables the experimental compositor path when `--enable-compositor-capture` is set and `CompositorCaptureBridge` can load
    the helper DLL, or
  * subscribes to `ChromiumWebBrowser.Paint` and starts a `FramePump` watchdog. On-demand pacing is used when the pipeline asks
    for paced invalidations; otherwise the pump free-runs at the requested cadence (Smoothness rides the 240 fps windowless rate
    by default, or the NDI cadence when `--smoothness-pump-output-rate` is set) and the watchdog injects recovery paints when
    timestamps stall.
* **Video path:** Both paint- and compositor-driven captures surface a `CapturedFrame`. When buffering is disabled the pipeline
  sends those frames directly to `INdiVideoSender` (one frame per pacing slot). With buffering enabled, frames are copied into a
  pooled `FrameRingBuffer<NdiVideoFrame>` once the buffer is primed. `EnsureCpuAccessible` currently drops GPU-only textures,
  so compositor capture falls back silently unless the helper supplies CPU memory. Pacing tracks capture/output cadence, issues
  invalidation “tickets” to throttle Chromium, and can pause capture entirely when backlog crosses the high-water mark. NDI
  video frames now always advertise the configured frame-rate fraction instead of deriving it from recent capture timestamps so
  receivers see a stable 30/1 (or 29.97/1.001, etc.) even when Chromium paints slightly fast.
* **Audio path:** `CustomAudioHandler` allocates one second of float storage, copies each planar channel into its own contiguous
  block inside that buffer, and forwards it to `NDIlib.send_send_audio_v2`. Metadata still reports “interleaved” while the
  payload remains pseudo-planar, so receivers must tolerate the mismatch.
* **Control plane:** ASP.NET Core minimal APIs listen on HTTP (no TLS, no auth). Swagger UI is always on. Endpoints dispatch
  directly into the singleton `Program.browserWrapper`.
* **KVM metadata:** The app advertises `<ndi_capabilities ntk_kvm="true" />`, then polls `NDIlib.send_capture` every second.
  `0x03` updates normalized mouse coordinates; `0x04` triggers a left-click via `CefWrapper.Click` (mouse down/up with a 100 ms
  pause). Other opcodes are ignored on purpose.
* **Shutdown:** After `app.Run()` completes the metadata thread is cancelled (with a hard join fallback), `CefWrapper.Dispose`
  tears down compositor/paint handlers, `Cef.Shutdown()` runs when it was initialized, the paced pipeline is disposed, and the
  temporary cache directory is deleted. The code now destroys the unmanaged sender (`NDIlib.send_destroy`), calls
  `NDIlib.destroy()`, frees the dynamically loaded NDI library, and resets `Program.NdiSenderPtr`.

---

## 2. Command-line contract & configuration defaults

`Program.Main` recognises the following switches (case-sensitive, leading double-hyphen required unless noted otherwise):

| Flag | Example | Behaviour / Default |
| --- | --- | --- |
| `--ndiname=<value>` | `--ndiname="Studio Browser"` | Sets the NDI source name. If omitted, the app prompts on STDIN until the
user types a non-empty name. Initial default is "HTML5" before prompting. |
| `--port=<int>` | `--port=9999` | Sets the HTTP listener port. If omitted, prompts interactively until a valid integer is entered. |
| `--url=<https://...>` | `--url=https://testpattern.tractusevents.com/` | Sets the startup page. Defaults to `https://testpattern.tractusevents.com/`. |
| `--w=<int>` | `--w=1920` | Sets browser width in pixels. Defaults to 1920. |
| `--h=<int>` | `--h=1080` | Sets browser height in pixels. Defaults to 1080. |
| `--fps=<double\|fraction>` | `--fps=59.94` | Target NDI frame cadence. Accepts decimal or rational values (e.g. `60000/1001`). Defaults to 60 fps. |
| `--buffer-depth=<int>` | `--buffer-depth=3` | Enables the paced output buffer with the specified capacity. A depth of 0 leaves buffering off. |
| `--enable-output-buffer` | `--enable-output-buffer` | Convenience flag to enable paced buffering with the default depth (3 frames, ≈`3 / fps` seconds once primed). |
| `--allow-latency-expansion` | `--allow-latency-expansion` | Lets the paced buffer drain queued frames before repeating the last send after an underrun, trading recovery smoothness for temporary extra latency. |
| `--disable-capture-alignment` | `--disable-capture-alignment` | Turns off capture timestamp alignment when pacing is enabled. Use `--align-with-capture-timestamps` to re-enable it explicitly. |
| `--disable-cadence-telemetry` | `--disable-cadence-telemetry` | Suppresses cadence jitter telemetry in logs. Use `--enable-cadence-telemetry` to force-enable it. |
| `--enable-paced-invalidation` / `--disable-paced-invalidation` | `--enable-paced-invalidation` | Couples Chromium invalidation with the paced sender. Each send slot triggers at most one capture. The disable form forces legacy free-run invalidation even if other inputs request pacing. |
| `--enable-capture-backpressure` / `--disable-capture-backpressure` | `--enable-capture-backpressure` | Pauses Chromium invalidation while the paced buffer sits above its high-water mark. Ignored when pacing is disabled. |
| `--enable-pump-cadence-adaptation` / `--disable-pump-cadence-adaptation` | `--enable-pump-cadence-adaptation` | Allows the pacing scheduler to stretch or delay invalidations using capture/output drift telemetry. |
| `--smoothness-pump-windowless-rate` / `--smoothness-pump-output-rate` | `--smoothness-pump-windowless-rate` | Chooses whether Smoothness mode drives the frame pump from the ~240 fps windowless render cadence (default) or the NDI output cadence. |
| `--telemetry-interval=<seconds>` | `--telemetry-interval=10` | Seconds between video pipeline telemetry log entries. Defaults to 10. |
| `--windowless-frame-rate=<double>` | `--windowless-frame-rate=60` | Overrides Chromium's internal repaint cadence. Defaults to the rounded value of `--fps`. |
| `--disable-gpu-vsync` | `--disable-gpu-vsync` | Passes `--disable-gpu-vsync` to Chromium to remove GPU vsync throttling. |
| `--disable-frame-rate-limit` | `--disable-frame-rate-limit` | Passes `--disable-frame-rate-limit` to Chromium for maximum redraw throughput. |
| `--enable-gpu-rasterization` | `--enable-gpu-rasterization` | Forces Chromium to rasterize content on the GPU for off-screen rendering scenarios. |
| `--enable-zero-copy` | `--enable-zero-copy` | Enables Chromium's zero-copy raster uploads so textures can be shared without an extra copy. |
| `--enable-oop-rasterization` | `--enable-oop-rasterization` | Moves raster work to the out-of-process raster thread. Alias: `--enable-out-of-process-rasterization`. |
| `--disable-background-throttling` | `--disable-background-throttling` | Prevents Chromium from throttling timers or hidden renderers. Also sets `--disable-renderer-backgrounding`. |
| `--preset-high-performance` | `--preset-high-performance` | Enables a preset of Chromium flags for maximum rendering throughput. This is equivalent to enabling `--enable-gpu-rasterization`, `--enable-zero-copy`, `--enable-oop-rasterization`, `--disable-gpu-vsync`, `--disable-frame-rate-limit`, and `--disable-background-throttling`. |
| `--preview` / `--enable-preview` / `--disable-preview` | `--preview` | Controls whether the interactive desktop preview window is displayed. When enabled, renders live OSR frames and translates/forwards mouse and keyboard input directly into Chromium while keeping NDI active. Enabled by default in the launcher. |
| `--pacing-mode=<Latency|Smoothness>` | `--pacing-mode=Smoothness` | Selects the paced sender strategy. The default `Latency` mode keeps the shallow paced buffer contract, while `Smoothness` tries to prioritise continuous motion with a deep buffer and high windowless render rate; when Smoothness is chosen without an explicit depth it now forces paced buffering with a 300-frame capacity even if buffering was already enabled via flag, and the launcher preserves previously saved Smoothness buffer depths when reopening settings (only reapplying the 300-frame default after the user reselects Smoothness). |
| `--enable-compositor-capture` / `--disable-compositor-capture` | `--enable-compositor-capture` | Opts into the compositor capture experiment guarded by `CompositorCaptureBridge`. When the helper DLL is missing or the toggle is off the app reverts to the legacy paint path. |
| `-debug` | `-debug` | Raises Serilog minimum level to `Debug`. |
| `-quiet` | `-quiet` | Disables console logging (file logging remains). |

Other configuration surfaces:

* Logging: Serilog writes to console (unless `-quiet`) and to `%USERPROFILE%/Documents/<AppName>_log.txt`. `AppManagement.LoggingLevel` is globally accessible.
* Build target: `Tractus.HtmlToNdi.csproj` targets **.NET 8.0**, `AllowUnsafeBlocks=true`, and forces `PlatformTarget=x64`. Do not assume .NET 6/7.
* The app project excludes everything under `Tests/` from its default `Compile/None/Content` globs so solution builds do not accidentally compile the xUnit sources or their generated `obj` artifacts. Keep test-only assets inside `Tests/` (or a sibling directory) to preserve this separation.
* Assets copied at runtime: `HtmlToNdi.ico` and `HtmlToNdi.png` are always copied to the output directory. The compositor helper DLL must accompany the executable when the experiment is enabled.

---

## 3. Repository map (authoritative)

```
Program.cs                           # Entry point, launcher fallback, HTTP surface, NDI bootstrap/cleanup
AppManagement.cs                     # Logging bootstrap, per-app data helpers, CLI flags (-debug/-quiet)
Launcher/
  LaunchParameters.cs                # CLI/launcher parsing, flag normalization, persisted settings conversion
  LauncherForm.cs                    # WinForms UI for configuring launch settings
  LauncherSettings*.cs               # JSON persistence for launcher defaults
Chromium/
  AsyncContext.cs                    # Single-threaded async pump for CefSharp startup
  BrowserPreviewForm.cs              # Interactive WinForms preview window fallback
  IBrowserPreviewWindow.cs           # Preview window abstraction for frame updates
  CustomPermissionHandler.cs         # IPermissionHandler auto-granting webcam, microphone, and permission prompts
  FramePump.cs                       # Periodic/on-demand invalidator with watchdog and cadence adaptation
  CefWrapper.cs                      # Owns ChromiumWebBrowser instance, paint/compositor bridge, HTTP input helpers
  CustomAudioHandler.cs              # IAudioHandler implementation, planar float → pseudo-interleaved buffer → NDI audio
  SingleThreadSynchronizationContext.cs # BlockingCollection-backed synchronization context
Preview/
  BrowserPreviewWindow.axaml(.cs)    # Frameless Avalonia preview window with unified dark header and navigation
  BrowserViewControl.cs              # Avalonia custom control rendering CefSharp OSR frames with input forwarding
  PreviewApp.axaml(.cs)              # Avalonia preview application bootstrap with Semi.Avalonia dark theme
Video/
  CapturedFrame.cs                   # Captured frame metadata (monotonic + UTC timestamps, storage kind)
  CapturedFrameStorageKind.cs        # Describes CPU vs shared-texture/shared-memory payloads
  FrameRate.cs                       # Frame-rate parsing helpers (decimal/fraction) and metadata conversion
  FrameRingBuffer.cs                 # Drop-oldest ring buffer used by the paced pipeline
  FrameTimeAverager.cs               # Sliding-window FPS estimator for telemetry metadata
  INdiVideoSender.cs                 # Abstraction for sending frames to NDI (native + test doubles)
  NdiVideoFrame.cs                   # Unmanaged frame copies used by the paced buffer
  NdiVideoPipeline.cs                # Direct + buffered NDI pipeline with telemetry and compositor support
  NdiVideoPipelineOptions.cs         # Options for configuring buffering/telemetry/pacing
Native/
  CompositorCaptureBridge.cs         # Managed wrapper around the native compositor helper
  Processing.NDI.Lib.x64.dll         # Bundled NDI runtime (copied to output/runtimes during publish)
  CompositorCapture/                 # C++ helper project producing CompositorCapture.dll
Models/
  GoToUrlModel.cs                    # Payloads for /seturl and /keystroke endpoints
Tractus.HtmlToNdi.http               # HTTP sample collection — keep in sync with Program.cs routes
Docs/                                # Design notes, paced-buffer plans, evaluations
```

---

## 4. Execution flow in detail

```
Main
 ├─ AppManagement.Initialize(args)
 │    ├─ Ensure base-directory data folder exists
 │    ├─ Hook AppDomain/TaskScheduler/WinForms exception handlers into Serilog
 │    ├─ Configure Serilog sinks (console unless -quiet, Documents/<AppName>_log.txt)
 │    └─ Respect -debug / -quiet flags
 ├─ Remove launcher-only flags, optionally launch WinForms configuration UI
 ├─ Parse CLI flags (see §2) for dimensions, URL, fps, buffering, telemetry, compositor toggle, etc.
 ├─ Compute effective buffer depth & pacing toggles, build NdiVideoPipelineOptions
 ├─ Ensure NDI native runtime is available (packaged runtimes/ first, then machine probe)
 ├─ Create NDI sender (NDIlib.send_create) and wrap it with `NdiVideoPipeline`
 ├─ AsyncContext.Run(async)
 │    ├─ Configure CefSettings (RootCachePath=cache/<guid>, autoplay override, fps/vsync flags, EnableAudio)
 │    ├─ Cef.Initialize(settings)
 │    ├─ Instantiate CefWrapper(width, height, url, pipeline, frame rate)
 │    └─ Await InitializeWrapperAsync (sets WindowlessFrameRate, toggles audio mute, activates compositor or FramePump)
 ├─ Build WebApplication (Serilog integration, Swagger, unused authorization middleware)
 ├─ Advertise `<ndi_capabilities ntk_kvm="true" />`
 │    └─ Launch background thread polling NDI metadata (1 s timeout)
 ├─ Map HTTP routes directly to CefWrapper methods
 ├─ app.Run()   # blocks until shutdown
 ├─ On shutdown: cancel metadata thread, dispose CefWrapper, call Cef.Shutdown(), dispose pipeline
 ├─ Destroy NDI sender/runtime and free the dynamically-loaded library
 └─ Delete temporary Cef cache directory (best-effort)
```

Global state (`Program.NdiSenderPtr`, `Program.browserWrapper`) means only **one** browser/NDI sender per process. Changes that
introduce additional instances must restructure the program.

---

## 5. HTTP API surface (current, unauthenticated)

All routes live in `Program.cs` and act on the singleton `browserWrapper`.

| Route | Method | Payload | Effect |
| --- | --- | --- | --- |
| `/seturl` | POST | JSON `{ "url": "https://..." }` (`GoToUrlModel`) | Calls `CefWrapper.SetUrl`, immediately loading the page. |
| `/scroll/{increment}` | GET | Path `increment` (int) | Sends `SendMouseWheelEvent(0,0,0,increment)` – positive values scroll down. Origin is always `(0,0)`; no viewport-relative targeting. |
| `/click/{x}/{y}` | GET | Path `x`, `y` (int pixels) | Sends a left mouse click (down, 100 ms sleep, up) at the specified coordinates. No bounds checking. |
| `/keystroke` | POST | JSON `{ "toSend": "..." }` (`SendKeystrokeModel`) | Iterates characters, firing `KeyDown` events for each Unicode code point (no key-up, modifiers, or special keys). |
| `/type/{toType}` | GET | Path string | Convenience wrapper around `/keystroke` (same limitations). |
| `/refresh` | GET | none | Calls `ChromiumWebBrowser.Reload()`. |

Swagger/OpenAPI UI is exposed at `/swagger`. There is no authentication, encryption, or rate limiting; never expose this service
to untrusted networks without an upstream proxy.

When adding routes, update **both** this table and `Tractus.HtmlToNdi.http` samples.

---

## 6. Subsystem specifics & quirks

### CefWrapper (`Chromium/CefWrapper.cs`)
* `ChromiumWebBrowser` is constructed with `AudioHandler = new CustomAudioHandler()` and a fixed `System.Drawing.Size(width,height)`.
* A pacing-aware `FramePump` invalidates Chromium on the cadence derived from `--fps` (or `--windowless-frame-rate`). When paced
  invalidation is enabled the pump runs in on-demand mode and waits for `NdiVideoPipeline` to request each capture slot; otherwise
  it free-runs on the configured interval. The watchdog issues recovery invalidations if paints stall, refreshing its timestamp
  only on real paint callbacks (or after `Resume`) so Chromium must produce a frame before the stall timer resets.
* **Experimental compositor capture** lives behind the `--enable-compositor-capture` CLI flag and the matching launcher checkbox
  (labelled “Compositor Capture (Experimental)”). The helper disables Chromium’s auto begin frames before starting the native
  session and restores them when the bridge stops. If the native DLL is missing or refuses to start, CefWrapper falls back to the
  legacy paint-driven path and logs a warning.
* `ScrollBy` always uses `(x=0,y=0)` as the mouse location; complex scrolling (e.g., inside scrolled divs) may require additional
  API work.
* `Click` only supports the left mouse button; drag, double-click, or right-click interactions are not implemented. The handler
  sends explicit down/up pairs with a short delay to mimic user clicks.
* `SendKeystrokes` issues **only** `KeyDown` events with `NativeKeyCode=Convert.ToInt32(char)`. There is no key-up, modifiers, or
  IME support—uppercase letters require the page to handle them despite missing Shift state.
* `Dispose` detaches paint/compositor handlers, stops the `FramePump`, restores Chromium begin-frame behaviour if needed, and
  releases the `NdiVideoPipeline`.

### Compositor capture helper (`Native/CompositorCaptureBridge.cs` & `Native/CompositorCapture/`)
* `CompositorCaptureBridge` wraps the native helper (`CompositorCapture.dll`). The managed side pins a callback delegate, starts
  the session, and listens for frames surfaced via `FrameArrived`.
* The helper tolerates a null `CefBrowserHost*`; begin-frame scheduling remains under managed control. Failures when loading the
  DLL (missing file, wrong architecture, missing exports) are logged and the capture path reverts automatically.
* Native frames currently need to provide CPU-accessible BGRA data. GPU-only textures are rejected by `NdiVideoPipeline` until the
  bridge maps them to CPU memory.

### CustomAudioHandler (`Chromium/CustomAudioHandler.cs`)
* `ChannelLayoutToChannelCount` covers most layouts but returns 0 for unsupported ones, causing `GetAudioParameters` to fail
  (muting audio).
* `OnAudioStreamPacket` copies planar floats into a pre-allocated buffer with each channel occupying a contiguous block (pseudo-
  planar). `channel_stride_in_bytes` is set to a single channel’s byte count; ensure downstream consumers accept that layout.
* Memory is manually allocated/freed via `Marshal.AllocHGlobal` / `FreeHGlobal`. Failing to call `Dispose` will leak unmanaged memory.

### NDI integration (`Program.cs`)
* `EnsureNdiNativeLibraryLoaded` first attempts to load the packaged runtime under `runtimes/win-<arch>/native`, then probes common
  install locations (`Program Files\NDI\...`, SDK paths, `NDILIB_REDIST_FOLDER`, etc.). It wires a `DllImportResolver`, augments
  `PATH`, and remembers candidates so error messages list every path that was tried.
* `Program.NdiSenderPtr` is created before Chromium bootstraps so the `NdiVideoPipeline` can send immediately. Shutdown now calls
  `NDIlib.send_destroy`, `NDIlib.destroy()`, and frees the native library handle while tolerating runtimes that omit those exports.
* Metadata loop logs every metadata frame at `Warning` level (`Log.Logger.Warning("Got metadata: ...")`), which can flood logs if
  receivers send frequent updates.
* Only opcodes `0x03` (mouse move) and `0x04` (left click) are handled; `0x07` (mouse up) is acknowledged but intentionally ignored.
  There is no translation for scroll, keyboard, or multi-button events.

### Frame buffering and pacing (`Video/NdiVideoPipeline.cs`)
* `FrameRingBuffer<T>` drops the oldest frame when capacity is exceeded and surfaces it via the `out` parameter. That action
  increments `DroppedFromOverflow` but does **not** dispose the frame; the caller must do so.
* `NdiVideoPipeline` owns the pacing scheduler that throttles Chromium, measures drift, and optionally pauses capture altogether
  when backlog exceeds the configured depth. In direct-send mode the scheduler still requests the next capture immediately after
  each transmission so Chromium cannot outrun the paced cadence. Backpressure counters (`captureGatePauses` / `captureGateResumes`)
  reset alongside the pacing state machine to keep telemetry accurate.
* Outstanding invalidations are represented by explicit tickets that share a single counter across direct pacing, buffered pacing,
  and capture backpressure. Tickets expire after a short timeout—unless the scheduler is explicitly paused—in which case expiration
  is suppressed to avoid churn. When a timeout does fire the pipeline trims stale entries and re-requests demand immediately so
  Chromium keeps drawing after stalls.
* Compositor frames flow through `HandleCompositorFrame`. They skip invalidation ticket checks but still contribute to telemetry.
  Frames with non-CPU storage kinds are dropped (with warnings) until CPU mapping is implemented.
* Direct paced sends run a maintenance loop that periodically tops up pending invalidations whenever pacing is active. The loop
  honours the capture gate and scheduler pause state so it never overloads Chromium while still preventing the demand counter from
  draining to zero when receivers slow down.
* Direct sends keep the most recently transmitted frame alive when the NDI sender requires buffer retention (e.g., async sends)
  and dispose it on the next transmission or pipeline stop, preventing freed memory from being reused by the native async API.
* Paced output deadlines are driven by a high-resolution waitable timer created with `CreateWaitableTimerEx` / `SetWaitableTimerEx`.
  When the platform cannot honour those APIs the scheduler falls back to the stopwatch path and busy-waits through the final
  ~16 ms instead of calling `Task.Delay`, sidestepping the coarse Windows timer quantum so short waits do not overshoot while
  still logging the downgrade once.

### Logging & diagnostics (`AppManagement.cs`)
* `AppManagement.InstanceName` composes `<os>_<arch>_<machinename>` for telemetry or metadata (not currently used elsewhere).
* On fatal `AppDomain` exceptions, details are logged but the process is not explicitly terminated beyond .NET’s default behaviour.
* Telemetry entries now include `captureCadencePercent`, `captureCadenceShortfallPercent`, and `captureCadenceFps` once Chromium paints have settled (buffer primed and roughly two seconds of cadence history). Emission is deferred until those cadence metrics are ready so the first log after warmup already carries the cadence snapshot, even when cadence jitter telemetry is disabled.
* Telemetry now also reports the active stopwatch clock precision (nanoseconds per tick plus the high-resolution flag), and the launcher surfaces the measured precision alongside other pacing controls.

---

## 7. Known limitations (validated against source)

1. **Single-instance design:** Global static `browserWrapper`/`NdiSenderPtr` make multiple concurrent browsers impossible without
   a refactor.
2. **Authentication void:** HTTP API is wide open. Use reverse proxies or add middleware before shipping to production.
3. **Input fidelity gaps:** No key-up events, modifiers, IME, or right/middle mouse buttons. `/scroll` ignores pointer position.
   Drag-and-drop is unsupported.
4. **Codec constraints:** Standard Chromium build without proprietary codecs—DRM/H.264/YouTube may fail.
5. **Audio layout:** Output buffer is not truly interleaved despite metadata suggesting so, which may confuse strict NDI consumers.
6. **Compositor capture packaging:** The experimental path requires `CompositorCapture.dll` alongside the executable and currently
   drops GPU-only frames. Expect the flag to fall back silently if the helper is missing or incompatible.
7. **Logging noise:** All incoming KVM metadata is logged at warning level, which may clutter logs under active control.
8. **Smoothness pacing overrides:** Smoothness now rebinds derived runtime flags (paced invalidation, capture backpressure, and latency expansion) to the overridden options so the mode's behaviour aligns with its advertised defaults.

---

## 8. Extension wish-list (ordered by likely impact)

1. **Secure the HTTP surface** – add authentication, optional TLS, and better error responses. Consider ASP.NET minimal API filters
   or reverse proxy guidance.
2. **Runtime configuration endpoints** – `/size`, `/fps`, `/audio` toggles that safely recreate the Chromium instance and reconfigure
   NDI frames.
3. **Improved input/KVM parity** – handle drag (distinct down/up), right-click, mouse move via HTTP, keyboard modifiers, and translate
   additional NDI KVM opcodes.
4. **Audio correctness** – produce genuinely interleaved buffers (or update metadata) and expose sample-rate/channel status via diagnostics.
5. **Observability** – add `/stats` endpoint exposing render cadence, last paint timestamp, audio packet counts, and cache path.
6. **Harden compositor capture** – ship the native helper with builds, support GPU texture mapping, and expose telemetry so operators
   know whether the experiment is active.

---

## 9. Validation checklist (run manually after significant changes)

* ✅ Verify transparency by loading `https://testpattern.tractusevents.com/` and checking alpha in an NDI receiver.
* ✅ Stress-test animation (e.g., WebGL or CSS animation) and confirm frame cadence ~16.6 ms (60 fps) without dropped frames.
* ✅ Play an audio source with known stereo content and ensure both channels arrive with correct timing.
* ✅ Exercise every HTTP route via `Tractus.HtmlToNdi.http` or Swagger (set URL, scroll, click, type, refresh) and watch logs for errors.
* ✅ Confirm KVM metadata click-from-receiver works (e.g., NewTek Studio Monitor sending mouse move + click).
* ✅ Inspect `%USERPROFILE%/Documents/<AppName>_log.txt` for warnings/errors after a session.

---

## 10. Pointers for future contributors

* Stick to the single-instance assumption unless refactoring `Program` comprehensively; many helpers assume globals.
* Respect the CefSharp threading rules—initialization must continue to use `AsyncContext`/`SingleThreadSynchronizationContext`.
* When touching HTTP routes, update Swagger annotations (minimal API `.WithOpenApi()`), documentation (`README.md`,
  `Tractus.HtmlToNdi.http`, and this file).
* If modifying audio/video frame structures, review `CustomAudioHandler`, `CefWrapper.OnBrowserPaint`, and
  `NdiVideoPipeline.Handle*` together to keep NDI metadata consistent.
* Before merging, ensure Serilog messages remain informative (prefer structured logging over string concatenation where practical).

---

_Last reviewed against repository state in this workspace. Update sections promptly when behaviour changes._
