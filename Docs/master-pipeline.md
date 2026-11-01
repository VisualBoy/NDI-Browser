# Tractus.HtmlToNdi master pipeline guide

_Last updated for the .NET 8 codebase captured in this workspace._

## Table of contents
1. [Mission and positioning](#1-mission-and-positioning)
2. [Architecture map](#2-architecture-map)
3. [Lifecycle walkthrough](#3-lifecycle-walkthrough)
4. [Configuration, flags, and defaults](#4-configuration-flags-and-defaults)
5. [Video subsystem](#5-video-subsystem)
6. [Audio subsystem](#6-audio-subsystem)
7. [Control surfaces and operator workflows](#7-control-surfaces-and-operator-workflows)
8. [Telemetry, logging, and observability](#8-telemetry-logging-and-observability)
9. [Automated and manual quality gates](#9-automated-and-manual-quality-gates)
10. [Development history and pacing rationale](#10-development-history-and-pacing-rationale)
11. [Operational risks and mitigations](#11-operational-risks-and-mitigations)
12. [Build, packaging, and deployment](#12-build-packaging-and-deployment)
13. [Operational checklist](#13-operational-checklist)

## 1. Mission and positioning
Tractus.HtmlToNdi is a Windows-only .NET 8 utility that renders an off-screen Chromium surface, republishes the visuals and audio over NewTek's NDI protocol, and exposes limited KVM control through HTTP endpoints and NDI metadata. The process assumes a single running instance: global state tracks the CefSharp browser, the unmanaged NDI sender pointer, the pacing-aware video pipeline, and the ASP.NET Core host until shutdown.【F:Program.cs†L55-L521】 The goal is to produce a predictable, broadcast-ready cadence while keeping operator controls simple enough for live production crews.

## 2. Architecture map
The solution can be understood as six collaborating subsystems:

| Subsystem | Responsibilities | Key artefacts |
| --- | --- | --- |
| Startup & configuration | Sanitise CLI arguments, bootstrap logging, optionally surface the WinForms launcher, and normalise launch parameters. | `Program.Main`, `RunApplication`, `LauncherForm`, `LaunchParameters`【F:Program.cs†L55-L227】【F:Launcher/LaunchParameters.cs†L151-L357】 |
| Chromium host | Create the off-screen browser, wire paint/audio hooks, expose scroll/click/keystroke helpers, and manage the paced `FramePump`. When compositor capture is enabled the native helper (`CompositorCapture.dll`) streams frames directly from Chromium's compositor instead of invalidation-driven paints. | `Chromium/CefWrapper.cs`, `Chromium/FramePump.cs`, `Native/CompositorCaptureBridge.cs`【F:Chromium/CefWrapper.cs†L11-L256】【F:Chromium/FramePump.cs†L60-L380】【F:Native/CompositorCaptureBridge.cs†L1-L235】 |
| Video pipeline | Translate Chromium paints into `CapturedFrame` instances, buffer or direct-send frames, coordinate paced invalidation tickets, and surface telemetry. | `Video/NdiVideoPipeline.cs`, `Video/FrameRingBuffer.cs`, `Video/NdiVideoPipelineOptions.cs`【F:Video/NdiVideoPipeline.cs†L202-L420】 |
| Audio pipeline | Convert planar Cef audio into contiguous buffers, marshal into `NDIlib.audio_frame_v2_t`, and ship through the native sender. | `Chromium/CustomAudioHandler.cs`【F:Chromium/CustomAudioHandler.cs†L10-L186】 |
| Control plane | Serve the HTTP API, map requests onto the browser helper methods, and run the NDI metadata thread that advertises and consumes limited KVM input. | `Program.cs` (ASP.NET mapping, metadata loop)【F:Program.cs†L279-L521】 |
| Observability & resilience | Apply Serilog sinks, integrate unhandled exception handlers, and manage telemetry counters for buffering, pacing, and invalidation health. | `AppManagement.cs`, `Program.cs`, `Video/NdiVideoPipeline.cs`【F:AppManagement.cs†L11-L199】【F:Program.cs†L55-L139】【F:Video/NdiVideoPipeline.cs†L202-L517】 |

All components run inside a single process using CefSharp's single-threaded synchronisation context (`AsyncContext` + `SingleThreadSynchronizationContext`) to keep Chromium happy while the ASP.NET Core host serves control traffic on the thread pool.【F:Program.cs†L231-L309】【F:Chromium/AsyncContext.cs†L1-L78】

## 3. Lifecycle walkthrough
1. **Bootstrap** – `Program.Main` wires process-wide exception handlers, switches into the executable directory, initialises Serilog via `AppManagement.Initialize`, and decides whether to show the WinForms launcher or parse CLI parameters directly.【F:Program.cs†L55-L227】【F:AppManagement.cs†L145-L199】 If the launcher is used, presets persist to `launcher-settings.json`; otherwise, CLI parsing produces a `LaunchParameters` record so every downstream stage consumes the same shape of configuration.【F:Program.cs†L87-L178】
2. **NDI provisioning** – `RunApplication` ensures the native runtime is present, allocates the unmanaged sender (`NDIlib.send_create`), and stores the pointer on `Program.NdiSenderPtr` for use by both the video and audio paths.【F:Program.cs†L185-L399】
3. **Chromium startup** – The process enters `AsyncContext.Run`, creates a `ChromiumWebBrowser` with audio enabled, configures frame-rate-related command-line switches, and instantiates `CefWrapper`. During `InitializeWrapperAsync` the browser loads the start URL, unmutes audio, and either enables the compositor capture helper (disabling `SetAutoBeginFrameEnabled`) or attaches the legacy paint handler before spinning up the pacing-aware `FramePump` that invalidates Chromium periodically or on demand.【F:Program.cs†L231-L309】【F:Chromium/CefWrapper.cs†L40-L144】【F:Chromium/FramePump.cs†L60-L220】
4. **Pipeline wiring** – When compositor capture is enabled the native bridge raises captured frames straight into `NdiVideoPipeline.HandleCompositorFrame`; otherwise `CefWrapper` forwards each `Paint` into `HandleFrame`. `CustomAudioHandler` copies planar floats into a contiguous buffer in both cases. The pipeline attaches the `FramePump` as its paced invalidation scheduler whenever the legacy path is active so capture cadence follows send demand.【F:Chromium/CefWrapper.cs†L43-L144】【F:Native/CompositorCaptureBridge.cs†L1-L235】【F:Chromium/CustomAudioHandler.cs†L121-L166】【F:Video/NdiVideoPipeline.cs†L202-L420】
5. **Control-plane host** – An ASP.NET Core minimal API binds to the configured port, exposes Swagger UI, and maps HTTP requests (URL changes, scroll, click, keystroke, refresh) straight to the singleton `CefWrapper`. In parallel a background thread advertises `<ndi_capabilities ntk_kvm="true"/>` and consumes `<ndi_kvm>` metadata to drive mouse clicks from compatible receivers.【F:Program.cs†L279-L521】
6. **Shutdown** – When the host stops, the metadata thread is cancelled, Chromium and the pipeline are disposed, outstanding pacing tasks are drained, and the temporary Cef cache is removed. Explicit `NDIlib.send_destroy` still needs to be added; today the OS releases the sender when the process exits.【F:Program.cs†L438-L521】

## 4. Configuration, flags, and defaults
All launch surfaces converge on `LaunchParameters.TryFromArgs`, which parses CLI switches, applies defaults, and enforces invariants (width/height > 0, valid URL, frame-rate parsing, etc.).【F:Launcher/LaunchParameters.cs†L151-L357】 The WinForms launcher serialises the same structure so toggles map 1:1 between GUI and CLI.【F:Launcher/LaunchParameters.cs†L361-L448】 Key switches are summarised below:

| Switch | Default | Effect |
| --- | --- | --- |
| `--ndiname=<value>` | Prompts until non-empty; initial value `HTML5` | Sets the advertised NDI source name and seeds the unmanaged sender handle.【F:Launcher/LaunchParameters.cs†L165-L225】【F:Program.cs†L185-L227】 |
| `--port=<int>` | Prompts (defaults to 9999) | Binds the ASP.NET Core host and Swagger UI to the chosen HTTP port.【F:Launcher/LaunchParameters.cs†L165-L244】【F:Program.cs†L279-L436】 |
| `--url=<https://...>` | `https://testpattern.tractusevents.com/` | Loads the initial page after Chromium warms up.【F:Launcher/LaunchParameters.cs†L246-L357】【F:Chromium/CefWrapper.cs†L40-L144】 |
| `--w=<int>` / `--h=<int>` | 1920×1080 | Sets the windowless browser surface size and the NDI frame dimensions.【F:Launcher/LaunchParameters.cs†L254-L268】【F:Chromium/CefWrapper.cs†L40-L144】 |
| `--fps=<double|fraction>` | 60 | Determines the target cadence, the paced sender's frame interval, and Chromium's windowless frame-rate override (unless manually set).【F:Launcher/LaunchParameters.cs†L270-L357】【F:Program.cs†L231-L309】 |
| `--buffer-depth=<int>` / `--enable-output-buffer` | 0 (direct send) | Enables the paced buffer, primes after `depth` frames, and enforces a fixed latency bucket.【F:Launcher/LaunchParameters.cs†L281-L357】【F:Video/NdiVideoPipeline.cs†L202-L420】 |
| `--allow-latency-expansion` | Off | Keeps queued frames playing during recovery instead of immediately repeating the last frame.【F:Launcher/LaunchParameters.cs†L337-L357】【F:Video/NdiVideoPipeline.cs†L216-L399】 |
| `--enable-paced-invalidation` / `--disable-paced-invalidation` | Off unless explicitly enabled | Couples Chromium invalidation to send demand. Disabling reverts to periodic invalidation even if buffering stays on.【F:Launcher/LaunchParameters.cs†L316-L357】【F:Video/NdiVideoPipeline.cs†L216-L420】 |
| `--enable-capture-backpressure` | Off | Pauses invalidations while backlog sits above the high-watermark; requires paced invalidation to be active.【F:Launcher/LaunchParameters.cs†L316-L357】【F:Video/NdiVideoPipeline.cs†L202-L420】 |
| `--enable-pump-cadence-adaptation` | Off | Lets the `FramePump` stretch or delay invalidations by up to half a frame using drift feedback from the pipeline.【F:Launcher/LaunchParameters.cs†L316-L357】【F:Chromium/FramePump.cs†L60-L220】 |
| `--enable-compositor-capture` | Off | Disables Chromium's auto begin-frame scheduling and lets the native compositor helper stream frames directly, bypassing the paced invalidation path. This mode is experimental and must remain opt-in until telemetry proves it stable.【F:Launcher/LaunchParameters.cs†L151-L357】【F:Chromium/CefWrapper.cs†L40-L144】【F:Native/CompositorCaptureBridge.cs†L1-L235】 |
| `--windowless-frame-rate=<double>` | Rounded `--fps` | Overrides Chromium's own repaint timer to better match odd cadences.【F:Launcher/LaunchParameters.cs†L322-L337】【F:Program.cs†L231-L309】 |
| `--disable-gpu-vsync` / `--disable-frame-rate-limit` | Off | Sends throughput-related flags into Chromium for stress scenarios.【F:Program.cs†L231-L309】 |
| `-debug` / `-quiet` | Off | Raises Serilog verbosity or mutes console logging while preserving file output.【F:AppManagement.cs†L145-L199】 |

Paced invalidation, capture backpressure, and cadence adaptation form a hierarchy: backpressure and adaptation rely on pacing to function and automatically disable themselves when pacing is off.【F:Video/NdiVideoPipeline.cs†L202-L420】

## 5. Video subsystem
### 5.1 Capture and ingestion
`ChromiumWebBrowser.Paint` events flow into `CefWrapper`, which wraps them as `CapturedFrame` instances and forwards them to `NdiVideoPipeline.HandleFrame`. When compositor capture is active the managed bridge raises identical `CapturedFrame` instances from the native helper into `HandleCompositorFrame`, skipping the paint-driven invalidation path entirely.【F:Chromium/CefWrapper.cs†L40-L144】【F:Native/CompositorCaptureBridge.cs†L1-L235】【F:Video/NdiVideoPipeline.cs†L351-L399】 The handler increments capture counters, validates pacing tickets when applicable, updates cadence tracking, and either sends directly or enqueues into the ring buffer depending on the configuration.【F:Video/NdiVideoPipeline.cs†L351-L399】 Direct mode is effectively zero-copy: the pipeline transmits immediately, then reissues the next paced invalidation when pacing is enabled.【F:Video/NdiVideoPipeline.cs†L369-L379】

### 5.2 Buffered pacing and latency guardrails
Buffered mode copies frames into unmanaged `NdiVideoFrame` structs, enqueues them in a `FrameRingBuffer`, and runs a long-lived pacing task once the backlog reaches the configured depth. Warm-up maintains a strict latency bucket by repeating the most recent frame until the queue is refilled, while oversupply trimming discards stale frames when producers run too far ahead. Optional latency expansion keeps queued frames playing before falling back to repeats. Each send updates counters for underruns, warm-up cycles, backlog hits, integrator values, and repeated frames so operators can audit pacing stability.【F:Video/NdiVideoPipeline.cs†L202-L517】

### 5.3 Invalidation scheduling
When pacing is active the pipeline issues `InvalidationTicket` objects that the `FramePump` consumes. `FramePump.RequestInvalidateAsync` queues requests through a channel, optionally delays them for cadence alignment, and finally calls `Cef.UIThreadTaskFactory.StartNew` to run `host.Invalidate(PaintElementType.View)` on Chromium's UI thread.【F:Video/NdiVideoPipeline.cs†L202-L420】【F:Chromium/FramePump.cs†L113-L380】 Tickets include timeouts; if the UI thread fails to service a request in time the pipeline treats it as expired, decrements pending counts, and re-primes capture demand so Chromium keeps drawing.【F:Video/NdiVideoPipeline.cs†L991-L1103】

### 5.4 UI events blocking paced invalidation (known issue)
Because both invalidation requests and user-driven UI events execute on the single Cef UI thread, bursts of input can temporarily starve the paced pipeline:

* **Trigger** – HTTP routes such as `/click`, `/keystroke`, `/type`, and `/scroll` call directly into `CefWrapper`, which synchronously invokes `SendMouseClickEvent`, `SendKeyEvent`, and `SendMouseWheelEvent` on the browser host. The click helper even pauses for 100 ms between down/up events to mimic a human dwell.【F:Program.cs†L403-L433】【F:Chromium/CefWrapper.cs†L184-L246】
* **Contention** – Each paced invalidation must marshal through `Cef.UIThreadTaskFactory.StartNew`, so the requests queue behind UI work. While the UI thread processes synchronous input, `RequestInvalidateAsync` holds its completion task open and the ticket timeout countdown continues.【F:Chromium/FramePump.cs†L113-L380】
* **Impact** – Once the timeout expires the pipeline flags `expiredInvalidationTickets`, reissues demand, and may log spurious capture counts because the browser paints without a matching ticket. Repeated expirations drain the buffer, switch the pipeline back into warm-up, and cause viewers to see repeated frames until Chromium catches up.【F:Video/NdiVideoPipeline.cs†L351-L517】【F:Video/NdiVideoPipeline.cs†L991-L1103】
* **Reproduction** – Issue rapid `/keystroke` requests (or hold a key inside the launcher) while paced invalidation and capture backpressure are enabled. Telemetry will show `expiredInvalidationTickets` increments and `captureGatePauses` as the buffer drains. Visual cadence stutters because invalidations resume only after the UI thread clears the input backlog.【F:Program.cs†L403-L436】【F:Video/NdiVideoPipeline.cs†L202-L517】
* **Mitigations** – For now operators should avoid high-frequency control bursts when pacing is active, or temporarily disable paced invalidation for heavy scripting sessions (accepting free-run cadence). Engineering options include queuing input through the same pacing scheduler, posting UI events asynchronously without sleeps, or increasing the ticket timeout once the UI-thread pressure is measurable.

Documenting this interaction ensures future work prioritises decoupling input injection from capture pacing so UI-heavy workloads cannot starve the invalidation queue.

## 6. Audio subsystem
`CustomAudioHandler` maps Chromium channel layouts to counts, allocates a one-second planar float buffer, and copies each channel contiguously before calling `NDIlib.send_send_audio_v2`. The handler leaves buffers in pseudo-planar layout (stride equals one channel), so receivers must tolerate sequential channels even though metadata claims interleaving. Memory is manually allocated and freed; failing to dispose leaks unmanaged buffers.【F:Chromium/CustomAudioHandler.cs†L10-L166】 Audio streaming honours `Program.NdiSenderPtr`, so if the sender fails to initialise audio silently drops until the pointer is non-zero.【F:Chromium/CustomAudioHandler.cs†L121-L166】【F:Program.cs†L185-L227】

## 7. Control surfaces and operator workflows
### 7.1 HTTP API
The minimal API provides:

| Route | Verb | Behaviour |
| --- | --- | --- |
| `/seturl` | POST | Loads a new URL via `CefWrapper.SetUrl`, ignoring null/empty payloads. |
| `/scroll/{increment}` | GET | Sends a mouse wheel event anchored at (0,0). |
| `/click/{x}/{y}` | GET | Triggers a left-click with a 100 ms dwell at the provided coordinates. |
| `/keystroke` | POST | Sends raw `KeyDown` events for each character in the payload string. |
| `/type/{text}` | GET | Convenience wrapper that calls `/keystroke`. |
| `/refresh` | GET | Reloads the current page. |

Swagger is enabled for manual testing. Because the host runs unauthenticated HTTP, production deployments must sit behind a trusted reverse proxy or add middleware before exposing the API publicly.【F:Program.cs†L279-L521】

### 7.2 Launcher UI
The WinForms launcher mirrors the CLI options, persists presets, and ensures incompatible combinations (e.g., capture backpressure without pacing) remain disabled. Operators can toggle pacing, latency expansion, cadence telemetry, and UI visibility before launching headless mode.【F:Program.cs†L89-L178】【F:Launcher/LaunchParameters.cs†L337-L357】

### 7.3 NDI metadata KVM bridge
A dedicated thread advertises KVM capability and polls `NDIlib.send_capture` for metadata. Incoming payloads are decoded via `NdiKvmHandler` (supporting uncompressed `<ndi_kvm u="...">` and zlib-compressed `<ndi_kvm d="...">` formats). Opcode `0x03` updates cached normalised coordinates and forwards cursor motion via `SendMouseMove`. Button press and release opcodes (`0x04`/`0x07` for left, `0x05`/`0x08` for middle/wheel, `0x06`/`0x09` for right) dispatch discrete mouse-down and mouse-up events to Chromium, enabling full click-and-drag interactions. Opcode `0x0A` dispatches mouse wheel events scaled to standard click increments.

## 8. Telemetry, logging, and observability
Serilog writes to console (unless `-quiet`) and to `%USERPROFILE%/Documents/<AppName>_log.txt`. `AppManagement` exposes a global logging level, installs AppDomain and TaskScheduler exception hooks, and integrates WinForms exception reporting.【F:AppManagement.cs†L11-L199】【F:Program.cs†L55-L139】 The video pipeline records backlog depth, primed state, underruns, warm-up durations, repeated frames, cadence offsets, latency integrator values, capture gate transitions, compositor capture usage, and (optionally) cadence trackers for both capture and output.【F:Video/NdiVideoPipeline.cs†L202-L517】 When pacing is enabled, maintenance loops keep invalidation demand topped up and ticket expirations logged so engineers can diagnose stalls.【F:Video/NdiVideoPipeline.cs†L202-L517】 Telemetry strings now include `compositorCapture`, `compositorFrames`, `legacyInvalidationFrames`, and capture cadence summaries (`captureCadencePercent`, `captureCadenceShortfallPercent`, `captureCadenceFps`) once roughly two seconds of paint history is available (and, if buffering is active, the ring buffer has primed) so operators can compare throughput and spot paint-stage drops without changing tooling.【F:Video/NdiVideoPipeline.cs†L2066-L2140】

## 9. Automated and manual quality gates
The xUnit suite covers input validation, frame-rate parsing, frame pump scheduling, ring-buffer hygiene, and the broad spectrum of pacing behaviours including invalidation ticket maintenance, capture backpressure, and latency expansion. The accompanying `Docs/tests-overview.md` document enumerates each test with its intent so contributors know which scenarios already have coverage.【F:Docs/tests-overview.md†L1-L53】 Manual validation remains essential: verify alpha-channel rendering with the hosted test pattern, stress animations, confirm stereo audio balance, exercise every HTTP route, test KVM metadata clicks, and inspect logs for pacing anomalies after real-world sessions.【F:AGENTS.md†L196-L210】

## 10. Development history and pacing rationale
Pacing evolved across six pull requests evaluated in `Docs/paced-buffer-pr-evaluation.md`, which highlighted trade-offs between cadence smoothness and fixed latency. Early versions drained the buffer aggressively, collapsing latency after underruns; later revisions preserved backlog but replayed stale frames or over-counted underruns. The current design merges the best elements: strict warm-up gating, hysteresis-based latency guards, integrator-driven trimming, and explicit repeat logic. `Docs/paced-buffer-improvement-plan.md` captures the intended control strategy that ultimately landed in `NdiVideoPipeline`: enforce depth bounds, discard stale frames, integrate backlog error, and expose detailed telemetry for operators.【F:Docs/paced-buffer-pr-evaluation.md†L1-L118】【F:Docs/paced-buffer-improvement-plan.md†L1-L155】【F:Video/NdiVideoPipeline.cs†L202-L517】 The paced invalidation work then extended the concept by tying Chromium invalidations to send demand and introducing capture backpressure plus cadence adaptation hooks.【F:Video/NdiVideoPipeline.cs†L202-L420】【F:Chromium/FramePump.cs†L60-L380】

## 11. Operational risks and mitigations
* **UI-thread contention between pacing and input events** – Documented above; avoid rapid-fire control bursts or disable pacing temporarily until the scheduler can be reworked.【F:Chromium/FramePump.cs†L113-L380】【F:Chromium/CefWrapper.cs†L184-L246】【F:Video/NdiVideoPipeline.cs†L991-L1103】
* **Unauthenticated HTTP API** – The service exposes powerful controls on plain HTTP; run behind a trusted proxy or add authentication before internet exposure.【F:Program.cs†L279-L436】
* **Single-instance assumption** – Global static fields (browser wrapper, NDI sender pointer) prevent multiple concurrent instances without architectural changes.【F:Program.cs†L185-L521】
* **Input fidelity gaps** – No key-up events, modifiers, scroll anchoring, or right/middle clicks; plan additional APIs if higher-fidelity KVM is required.【F:Chromium/CefWrapper.cs†L184-L255】
* **Audio layout ambiguity** – Audio buffers remain pseudo-planar even though metadata advertises interleaving; downstream receivers must cope or the format should be corrected.【F:Chromium/CustomAudioHandler.cs†L121-L166】
* **Resource cleanup** – NDI handles and certain Cef resources rely on process exit; implement explicit teardown for long-running services.【F:Program.cs†L438-L521】

## 12. Build, packaging, and deployment
Build with the .NET 8 SDK plus the Windows Desktop workload (`dotnet build`). Tests run via `dotnet test`. Publishing for operators typically uses `dotnet publish -c Release -r win-x64 --self-contained false`, which bundles the NDI redistributable so users can unzip and run without installing extra runtimes. Linux/CI environments must install the WindowsDesktop workload to compile the WinForms launcher.【F:Docs/building.md†L1-L57】【F:README.md†L7-L80】

## 13. Operational checklist
Before releasing a new build or lighting up a production show:
- Launch the bundled test pattern and verify alpha-channel fidelity plus animation smoothness in an NDI receiver.【F:AGENTS.md†L196-L203】
- Play stereo audio content and confirm both channels arrive with the expected latency for the configured buffer depth.【F:AGENTS.md†L198-L204】【F:Chromium/CustomAudioHandler.cs†L121-L166】
- Exercise every HTTP route (manually or via `Tractus.HtmlToNdi.http`) and observe logs for pacing telemetry, ticket expirations, or UI-thread contention warnings.【F:Program.cs†L403-L436】【F:AGENTS.md†L204-L210】【F:Video/NdiVideoPipeline.cs†L202-L517】
- Validate NDI metadata clicks from a compatible receiver, watching both on-screen behaviour and log volume from metadata frames.【F:Program.cs†L297-L399】

Following this guide keeps the paced pipeline predictable, highlights current failure modes, and provides enough context for new contributors to evolve the system without trawling the repository history.
