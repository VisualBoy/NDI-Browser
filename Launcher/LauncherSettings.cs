namespace Tractus.HtmlToNdi.Launcher;

/// <summary>
/// Represents the settings for the launcher, which can be persisted.
/// </summary>
public class LauncherSettings
{
    /// <summary>
    /// Gets or sets the NDI source name.
    /// </summary>
    public string NdiName { get; set; } = "HTML5";

    /// <summary>
    /// Gets or sets the HTTP server port.
    /// </summary>
    public int Port { get; set; } = 9999;

    /// <summary>
    /// Gets or sets the startup URL.
    /// </summary>
    public string Url { get; set; } = "https://testpattern.tractusevents.com/";

    /// <summary>
    /// Gets or sets the width of the browser.
    /// </summary>
    public int Width { get; set; } = 1920;

    /// <summary>
    /// Gets or sets the height of the browser.
    /// </summary>
    public int Height { get; set; } = 1080;

    /// <summary>
    /// Gets or sets the target NDI frame rate as a string.
    /// </summary>
    public string FrameRate { get; set; } = "60";

    /// <summary>
    /// Gets or sets a value indicating whether the paced output buffer is enabled.
    /// </summary>
    public bool EnableBuffering { get; set; }
        = false;

    /// <summary>
    /// Gets or sets the capacity of the paced output buffer.
    /// </summary>
    public int BufferDepth { get; set; } = 3;

    /// <summary>
    /// Gets or sets the interval between video pipeline telemetry log entries in seconds.
    /// </summary>
    public double TelemetryIntervalSeconds { get; set; } = 10;

    /// <summary>
    /// Gets or sets an optional override for Chromium's internal repaint cadence.
    /// </summary>
    public string? WindowlessFrameRateOverride { get; set; }
        = null;

    /// <summary>
    /// Gets or sets a value indicating whether to disable Chromium's GPU vsync throttling.
    /// </summary>
    public bool DisableGpuVsync { get; set; }
        = true;

    /// <summary>
    /// Gets or sets a value indicating whether to disable Chromium's frame rate limiter.
    /// </summary>
    public bool DisableFrameRateLimit { get; set; }
        = true;

    /// <summary>
    /// Gets or sets a value indicating whether the paced buffer should keep playing any queued frames during recovery.
    /// </summary>
    public bool AllowLatencyExpansion { get; set; }
        = false;

    /// <summary>
    /// Gets or sets a value indicating whether the paced sender should align deadlines to capture timestamps.
    /// </summary>
    public bool AlignWithCaptureTimestamps { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to include capture/output cadence metrics in telemetry logs.
    /// </summary>
    public bool EnableCadenceTelemetry { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether Chromium invalidation should be paced by the sender loop.
    /// </summary>
    public bool EnablePacedInvalidation { get; set; }
        = false;

    /// <summary>
    /// Gets or sets a value indicating whether paced invalidation should be forcefully disabled.
    /// </summary>
    public bool DisablePacedInvalidation { get; set; }
        = false;

    /// <summary>
    /// Gets or sets a value indicating whether capture should pause when the paced buffer is oversupplied.
    /// </summary>
    public bool EnableCaptureBackpressure { get; set; }
        = false;

    /// <summary>
    /// Gets or sets a value indicating whether the Chromium pump adapts to cadence telemetry.
    /// </summary>
    public bool EnablePumpCadenceAdaptation { get; set; }
        = false;

    /// <summary>
    /// Gets or sets a value indicating whether the Smoothness frame pump should run at the windowless render cadence.
    /// </summary>
    public bool SmoothnessPumpAtWindowlessRate { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether compositor-driven capture should be enabled.
    /// </summary>
    public bool EnableCompositorCapture { get; set; }
        = false;

    /// <summary>
    /// Gets or sets a value indicating whether Chromium should force GPU rasterization.
    /// </summary>
    public bool EnableGpuRasterization { get; set; }
        = true;

    /// <summary>
    /// Gets or sets a value indicating whether Chromium should enable zero-copy raster uploads.
    /// </summary>
    public bool EnableZeroCopy { get; set; }
        = true;

    /// <summary>
    /// Gets or sets a value indicating whether Chromium should use the out-of-process rasterizer.
    /// </summary>
    public bool EnableOutOfProcessRasterization { get; set; }
        = true;

    /// <summary>
    /// Gets or sets a value indicating whether Chromium should keep timers and renderers active while hidden.
    /// </summary>
    public bool DisableBackgroundThrottling { get; set; }
        = true;

    /// <summary>
    /// Gets or sets a value indicating whether the high-performance preset should be enabled.
    /// </summary>
    public bool PresetHighPerformance { get; set; }
        = true;

    /// <summary>
    /// Gets or sets the pacing mode for the video pipeline.
    /// </summary>
    public PacingMode PacingMode { get; set; } = PacingMode.Latency;

    /// <summary>
    /// Gets or sets a value indicating whether the NDI sender should use the asynchronous send method.
    /// </summary>
    public bool NdiSendAsync { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the interactive preview window is enabled.
    /// </summary>
    public bool EnablePreview { get; set; } = true;
}
