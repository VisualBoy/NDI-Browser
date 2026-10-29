using Avalonia;
using Application = System.Windows.Forms.Application;
using CefSharp;
using CefSharp.OffScreen;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NewTek;
using NewTek.NDI;
using Serilog;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Tractus.HtmlToNdi.Chromium;
using Tractus.HtmlToNdi.Launcher;
using Tractus.HtmlToNdi.Models;
using Tractus.HtmlToNdi.Video;

namespace Tractus.HtmlToNdi;

/// <summary>
/// The main program class.
/// </summary>
public class Program
{
    /// <summary>
    /// A pointer to the NDI sender instance.
    /// </summary>
    public static nint NdiSenderPtr;
    internal static CefWrapper browserWrapper = null!;
    private static Thread? previewUiThread;

    private static readonly object NdiLibraryLock = new();
    private static bool NdiLibraryConfigured;
    private static string[] NdiLibraryCandidates = Array.Empty<string>();
    private static string[] NdiSearchDirectories = Array.Empty<string>();
    private static string? NdiBundledLibraryPath;
    private static readonly Stopwatch StartupStopwatch = Stopwatch.StartNew();
    private static nint NdiNativeLibraryHandle;

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod", SetLastError = true)]
    private static extern uint TimeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod", SetLastError = true)]
    private static extern uint TimeEndPeriod(uint uPeriod);

    private static int? appliedTimerResolution;

    private static void ApplySystemTuning(LaunchParameters parameters)
    {
        if (parameters.MmTimerResolutionMs is int periodMs && periodMs > 0)
        {
            var rc = TimeBeginPeriod((uint)periodMs);
            if (rc == 0)
            {
                appliedTimerResolution = periodMs;
                Log.Information("Multimedia timer resolution set to {PeriodMs} ms (timeBeginPeriod ok)", periodMs);
            }
            else
            {
                Log.Warning("timeBeginPeriod({PeriodMs}) returned {Rc} (TIMERR_NOCANDO=97). Continuing.", periodMs, rc);
            }
        }

        if (!string.IsNullOrWhiteSpace(parameters.GcLatencyMode))
        {
            var mode = parameters.GcLatencyMode.Trim().ToLowerInvariant() switch
            {
                "interactive" => System.Runtime.GCLatencyMode.Interactive,
                "low-latency" => System.Runtime.GCLatencyMode.LowLatency,
                "sustained-low-latency" => System.Runtime.GCLatencyMode.SustainedLowLatency,
                "batch" => System.Runtime.GCLatencyMode.Batch,
                _ => (System.Runtime.GCLatencyMode?)null,
            };
            if (mode is null)
            {
                Log.Warning("Unknown --gc-latency-mode value '{Value}'; ignoring", parameters.GcLatencyMode);
            }
            else
            {
                try
                {
                    // LowLatency is unsupported under Server GC (which the project enables);
                    // SustainedLowLatency is the supported equivalent. Catch the exception
                    // rather than aborting startup so a sweep config can still record an ERROR
                    // row instead of crashing the whole process.
                    System.Runtime.GCSettings.LatencyMode = mode.Value;
                    Log.Information("GCSettings.LatencyMode = {Mode}", mode.Value);
                }
                catch (InvalidOperationException ex)
                {
                    Log.Warning(ex, "Cannot set GCSettings.LatencyMode to {Mode} (likely incompatible with Server GC); leaving at {Current}", mode.Value, System.Runtime.GCSettings.LatencyMode);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(parameters.ProcessPriority))
        {
            var priority = parameters.ProcessPriority.Trim().ToLowerInvariant() switch
            {
                "high" => ProcessPriorityClass.High,
                "above-normal" => ProcessPriorityClass.AboveNormal,
                "normal" => ProcessPriorityClass.Normal,
                "below-normal" => ProcessPriorityClass.BelowNormal,
                "idle" => ProcessPriorityClass.Idle,
                "realtime" => ProcessPriorityClass.RealTime,
                _ => (ProcessPriorityClass?)null,
            };
            if (priority is null)
            {
                Log.Warning("Unknown --process-priority value '{Value}'; ignoring", parameters.ProcessPriority);
            }
            else
            {
                try
                {
                    Process.GetCurrentProcess().PriorityClass = priority.Value;
                    Log.Information("Process priority class = {Priority}", priority.Value);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to set process priority class to {Priority}", priority.Value);
                }
            }
        }

        if (parameters.CpuAffinityMask is ulong mask)
        {
            try
            {
                // ProcessorAffinity is nint (signed); on 64-bit hosts the
                // high-bit-set masks (e.g. 0x8000000000000000) only round-trip
                // through an unchecked cast - regular cast would throw an
                // OverflowException for legitimate single-top-CPU selections.
                Process.GetCurrentProcess().ProcessorAffinity = unchecked((nint)mask);
                Log.Information("Process CPU affinity = 0x{Mask:X}", mask);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to set process CPU affinity to 0x{Mask:X}", mask);
            }
        }
    }

    private static void RevertSystemTuning()
    {
        if (appliedTimerResolution is int periodMs)
        {
            TimeEndPeriod((uint)periodMs);
            appliedTimerResolution = null;
        }
    }

    private static void StartInteractivePreview(CefWrapper wrapper, int width, int height)
    {
        Log.Information("Launching interactive desktop preview window (Avalonia)");
        previewUiThread = new Thread(() =>
        {
            try
            {
                Tractus.HtmlToNdi.Preview.PreviewApp.PendingWrapper = wrapper;
                Tractus.HtmlToNdi.Preview.PreviewApp.PendingWidth = width;
                Tractus.HtmlToNdi.Preview.PreviewApp.PendingHeight = height;
                Tractus.HtmlToNdi.Preview.PreviewApp.PendingLogger = Log.Logger;

                Avalonia.AppBuilder.Configure<Tractus.HtmlToNdi.Preview.PreviewApp>()
                    .UsePlatformDetect()
                    .WithInterFont()
                    .StartWithClassicDesktopLifetime(Array.Empty<string>(), Avalonia.Controls.ShutdownMode.OnMainWindowClose);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Interactive preview window encountered an error");
            }
            finally
            {
                wrapper.DetachPreview();
            }
        })
        {
            IsBackground = true,
            Name = "InteractivePreviewUI"
        };
        previewUiThread.SetApartmentState(ApartmentState.STA);
        previewUiThread.Start();
    }

    private static void WarnAboutUnwiredFlags(LaunchParameters parameters)
    {
        // These flags are accepted by the CLI parser but their pipeline plumbing
        // is deferred to a follow-up session. They are intentionally no-ops today
        // so the sweep runner does not crash on configs that include them, but
        // they will not produce different results until wired through.
        if (parameters.PacerThreadPriority is not null)
            Log.Warning("--pacer-thread-priority={Value} is accepted but not yet wired to the pacer thread; ignored", parameters.PacerThreadPriority);
        if (parameters.CadenceAdaptationGain is not null)
            Log.Warning("--cadence-adapt-gain={Value} is accepted but not yet wired to FramePump; ignored", parameters.CadenceAdaptationGain);
        if (parameters.BufferOverflowPolicy is not null)
            Log.Warning("--buffer-overflow-policy={Value} is accepted but not yet wired to FrameRingBuffer; ignored", parameters.BufferOverflowPolicy);
        if (parameters.LatencyExpansionStrategy is not null)
            Log.Warning("--latency-expansion-strategy={Value} is accepted but not yet wired; --allow-latency-expansion still controls behavior", parameters.LatencyExpansionStrategy);
        if (parameters.PacedWarmupFrames is not null)
            Log.Warning("--paced-warmup-frames={Value} is accepted but not yet wired; ignored", parameters.PacedWarmupFrames);
    }

    /// <summary>
    /// The main entry point for the application.
    /// </summary>
    /// <param name="args">The command-line arguments.</param>
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (sender, eventArgs) =>
        {
            var exception = eventArgs.ExceptionObject as Exception;
            if (exception is not null)
            {
                Log.Fatal(exception, "AppDomain unhandled exception (IsTerminating={IsTerminating})", eventArgs.IsTerminating);
            }
            else
            {
                Log.Fatal("AppDomain unhandled exception object: {ExceptionObject} (IsTerminating={IsTerminating})", eventArgs.ExceptionObject, eventArgs.IsTerminating);
            }
        };

        TaskScheduler.UnobservedTaskException += (sender, eventArgs) =>
        {
            Log.Fatal(eventArgs.Exception, "Unobserved task exception captured");
            eventArgs.SetObserved();
        };

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (sender, eventArgs) =>
        {
            Log.Fatal(eventArgs.Exception, "WinForms UI thread exception");
        };

        var sanitizedArgs = RemoveLauncherFlags(args);
        var launchCachePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache", Guid.NewGuid().ToString());

        var exeDirectory = AppDomain.CurrentDomain.BaseDirectory;
        Directory.SetCurrentDirectory(exeDirectory);
        AppManagement.Initialize(sanitizedArgs);

        Log.Information("Startup arguments sanitized. Original={OriginalArgs} Sanitized={SanitizedArgs}", string.Join(' ', args), string.Join(' ', sanitizedArgs));

        LaunchParameters? parameters;
        if (ShouldUseLauncher(args))
        {
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var persistedSettings = LauncherSettingsStore.Load();
            using var launcherForm = new LauncherForm(persistedSettings);
            var dialogResult = launcherForm.ShowDialog();
            if (dialogResult != DialogResult.OK || launcherForm.SelectedParameters is null)
            {
                Log.Information("Launcher closed without starting the application.");
                return;
            }

            if (launcherForm.CurrentSettings is not null)
            {
                LauncherSettingsStore.Save(launcherForm.CurrentSettings);
            }

            parameters = launcherForm.SelectedParameters;
            Log.Information("Launcher accepted parameters: {@Parameters}", parameters);
        }
        else
        {
            if (!LaunchParameters.TryFromArgs(sanitizedArgs, out parameters) || parameters is null)
            {
                Log.Error("Failed to parse command-line parameters; exiting early");
                return;
            }

            Log.Information("Command-line parameters parsed: {@Parameters}", parameters);
        }

        // ApplySystemTuning sits inside the try/finally so that timeBeginPeriod
        // (a process-wide setting) is always reverted even if WarnAboutUnwiredFlags
        // or RunApplication throws before completing setup.
        try
        {
            ApplySystemTuning(parameters);
            WarnAboutUnwiredFlags(parameters);
            RunApplication(parameters, sanitizedArgs, launchCachePath);
        }
        catch (DllNotFoundException ex) when (ex.Message.Contains("NDI", StringComparison.OrdinalIgnoreCase))
        {
            Log.Fatal(ex, "Unable to start because the native NDI runtime was not found. {Reason}", ex.Message);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Fatal exception during startup");
        }
        finally
        {
            RevertSystemTuning();
            Log.Information("Program.Main exiting after {Elapsed}ms", StartupStopwatch.ElapsedMilliseconds);
        }
    }

    private static void RunApplication(LaunchParameters parameters, string[] args, string launchCachePath)
    {
        Log.Information("RunApplication starting with {@Parameters} (cache={CachePath})", parameters, launchCachePath);

        var useHighPerformancePreset = parameters.PresetHighPerformance;

        var frameRate = parameters.FrameRate;
        var width = parameters.Width;
        var height = parameters.Height;
        var startUrl = parameters.StartUrl;
        var windowlessFrameRateOverride = parameters.WindowlessFrameRateOverride;

        var enableBuffering = parameters.EnableBuffering;
        var effectiveDepth = enableBuffering ? Math.Max(1, parameters.BufferDepth == 0 ? 3 : parameters.BufferDepth) : 1;

        var pacedInvalidationEnabled = parameters.DisablePacedInvalidation
            ? false
            : parameters.EnablePacedInvalidation;

        if (parameters.DisablePacedInvalidation)
        {
            Log.Information("Paced invalidation disabled via configuration.");
        }

        var pipelineOptions = new NdiVideoPipelineOptions
        {
            EnableBuffering = enableBuffering,
            BufferDepth = effectiveDepth,
            TelemetryInterval = parameters.TelemetryInterval,
            AllowLatencyExpansion = parameters.AllowLatencyExpansion,
            AlignWithCaptureTimestamps = parameters.AlignWithCaptureTimestamps,
            EnableCadenceTelemetry = parameters.EnableCadenceTelemetry,
            EnablePacedInvalidation = pacedInvalidationEnabled,
            DisablePacedInvalidation = parameters.DisablePacedInvalidation,
            EnableCaptureBackpressure = parameters.EnableCaptureBackpressure,
            EnablePumpCadenceAdaptation = parameters.EnablePumpCadenceAdaptation,
            SmoothnessPumpAtWindowlessRate = parameters.SmoothnessPumpAtWindowlessRate,
            EnableCompositorCapture = parameters.EnableCompositorCapture,
            PacingMode = parameters.PacingMode,
        };

        NativeNdiVideoSender? ndiSender = null;
        NdiVideoPipeline? videoPipeline = null;
        CancellationTokenSource? metadataCancellation = null;
        Thread? metadataThread = null;
        bool metadataThreadStarted = false;
        bool pipelineAttachedToBrowser = false;

        Log.Information("Ensuring NDI native runtime is available...");
        EnsureNdiNativeLibraryLoaded();
        Log.Information("NDI native runtime setup complete");

        var ndiNamePtr = UTF.StringToUtf8(parameters.NdiName);
        try
        {
            var settings_T = new NDIlib.send_create_t
            {
                p_ndi_name = ndiNamePtr
            };

            try
            {
                Program.NdiSenderPtr = NDIlib.send_create(ref settings_T);
            }
            catch (DllNotFoundException ex)
            {
                var message = CreateNdiFailureMessage();
                Log.Fatal(ex, message);
                throw;
            }
        }
        finally
        {
            if (ndiNamePtr != nint.Zero)
            {
                Marshal.FreeHGlobal(ndiNamePtr);
            }
        }

        if (Program.NdiSenderPtr == nint.Zero)
        {
            Log.Error("Failed to create NDI sender. Exiting.");
            return;
        }

        try
        {
            Log.Information("NDI sender created successfully");

            ndiSender = new NativeNdiVideoSender(Program.NdiSenderPtr, parameters.NdiSendAsync);
            videoPipeline = new NdiVideoPipeline(ndiSender, frameRate, pipelineOptions, Log.Logger);

            try
            {
                Log.Information("Initialising Chromium via AsyncContext");
                AsyncContext.Run(async delegate
                {
                    var settings = new CefSettings();
                    settings.WindowlessRenderingEnabled = true;

                    if (!Directory.Exists(launchCachePath))
                    {
                        Directory.CreateDirectory(launchCachePath);
                    }

                    settings.RootCachePath = launchCachePath;
                    settings.CefCommandLineArgs.Add("autoplay-policy", "no-user-gesture-required");

                    var targetWindowlessRate = windowlessFrameRateOverride ?? Math.Clamp((int)Math.Round(frameRate.Value), 1, 240);
                    settings.CefCommandLineArgs.Add("off-screen-frame-rate", targetWindowlessRate.ToString(CultureInfo.InvariantCulture));

                    // GPU acceleration & WebGL optimizations (always on)
                    settings.CefCommandLineArgs["ignore-gpu-blocklist"] = "1";
                    settings.CefCommandLineArgs["enable-gpu-rasterization"] = "1";
                    settings.CefCommandLineArgs["enable-zero-copy"] = "1";
                    settings.CefCommandLineArgs["enable-oop-rasterization"] = "1";
                    settings.CefCommandLineArgs["disable-gpu-vsync"] = "1";
                    settings.CefCommandLineArgs["disable-frame-rate-limit"] = "1";
                    settings.CefCommandLineArgs["disable-background-timer-throttling"] = "1";
                    settings.CefCommandLineArgs["disable-renderer-backgrounding"] = "1";
                    settings.CefCommandLineArgs["enable-webgl"] = "1";
                    settings.CefCommandLineArgs["use-angle"] = "";
                    settings.CefCommandLineArgs["enable-accelerated-2d-canvas"] = "1";
                    settings.CefCommandLineArgs["enable-accelerated-video-decode"] = "1";

                    // Auto-grant media stream (webcam, mic)
                    settings.CefCommandLineArgs["enable-media-stream"] = "1";
                    settings.CefCommandLineArgs["use-fake-ui-for-media-stream"] = "1";

                    // Security & cross-origin policy relaxation for broadcast templates
                    //settings.CefCommandLineArgs["disable-web-security"] = "1";
                    settings.CefCommandLineArgs["remote-allow-origins"] = "*";
                    settings.CefCommandLineArgs["no-sandbox"] = "1";

                    // Deterministic frame pacing for broadcast pipelines
                    settings.CefCommandLineArgs["enable-begin-frame-scheduling"] = "0";

                    if (!parameters.DisableAudio)
                    {
                        settings.EnableAudio();
                    }

                    if (!string.IsNullOrWhiteSpace(parameters.CefExtraArgs))
                    {
                        // Use indexer assignment instead of Add() so user-supplied flags can
                        // override anything we set above (e.g. passing
                        // --cef-extra-args="disable-gpu-vsync=0" to undo the preset). Add()
                        // throws ArgumentException on duplicate keys and would crash CEF init.
                        foreach (var entry in parameters.CefExtraArgs.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            var eq = entry.IndexOf('=');
                            if (eq < 0)
                            {
                                settings.CefCommandLineArgs[entry] = "1";
                                Log.Information("Cef extra arg: --{Flag}", entry);
                            }
                            else
                            {
                                var key = entry[..eq].Trim();
                                var value = entry[(eq + 1)..].Trim();
                                if (!string.IsNullOrEmpty(key))
                                {
                                    settings.CefCommandLineArgs[key] = value;
                                    Log.Information("Cef extra arg: --{Flag}={Value}", key, value);
                                }
                            }
                        }
                    }
                    var cefInitialized = Cef.Initialize(settings);
                    Log.Information("CEF initialization {Result}", cefInitialized ? "succeeded" : "reported failure");
                    browserWrapper = new CefWrapper(
                        width,
                        height,
                        startUrl,
                        videoPipeline,
                        frameRate,
                        Log.Logger,
                        windowlessFrameRateOverride);
                    pipelineAttachedToBrowser = true;

                    await browserWrapper.InitializeWrapperAsync();
                    Log.Information("Chromium initialised successfully");

                    if (parameters.EnablePreview)
                    {
                        StartInteractivePreview(browserWrapper, width, height);
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Failed to initialize Chromium or the video pipeline.");
                return;
            }

            Log.Information("Building ASP.NET Core host on port {Port}", parameters.Port);
            var builder = WebApplication.CreateBuilder(args);

            builder.Services.AddSerilog();

            builder.WebHost.UseUrls($"http://*:{parameters.Port}");

            // Add services to the container.
            builder.Services.AddAuthorization();

            // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen();

            var app = builder.Build();
            app.UseSwagger();
            app.UseSwaggerUI();

            var capabilitiesXml = $$"""<ndi_capabilities ntk_kvm="true" />""";
            capabilitiesXml += "\0";
            var capabilitiesPtr = UTF.StringToUtf8(capabilitiesXml);

            var metaframe = new NDIlib.metadata_frame_t()
            {
                p_data = capabilitiesPtr
            };

            NDIlib.send_add_connection_metadata(NdiSenderPtr, ref metaframe);
            Marshal.FreeHGlobal(capabilitiesPtr);

            metadataCancellation = new CancellationTokenSource();
            var metadataToken = metadataCancellation.Token;
            metadataThread = new Thread(() =>
            {
                var metadata = new NDIlib.metadata_frame_t();
                var x = 0.0f;
                var y = 0.0f;
                Log.Information("NDI metadata capture thread started");
                try
                {
                    while (!metadataToken.IsCancellationRequested)
                    {
                        var senderHandle = Program.NdiSenderPtr;
                        if (senderHandle == nint.Zero)
                        {
                            if (metadataToken.WaitHandle.WaitOne(100))
                            {
                                break;
                            }

                            continue;
                        }

                        var result = NDIlib.send_capture(senderHandle, ref metadata, 1000);

                        if (metadataToken.IsCancellationRequested)
                        {
                            break;
                        }

                        if (result == NDIlib.frame_type_e.frame_type_none)
                        {
                            continue;
                        }
                        else if (result == NDIlib.frame_type_e.frame_type_metadata)
                        {
                            var metadataConverted = UTF.Utf8ToString(metadata.p_data);

                            if (metadataConverted.StartsWith("<ndi_kvm u=\"", StringComparison.Ordinal))
                            {
                                metadataConverted = metadataConverted.Replace("<ndi_kvm u=\"", string.Empty, StringComparison.Ordinal);
                                metadataConverted = metadataConverted.Replace("\"/>", string.Empty, StringComparison.Ordinal);

                                try
                                {
                                    var binary = Convert.FromBase64String(metadataConverted);

                                    var opcode = binary[0];

                                    if (opcode == 0x03)
                                    {
                                        x = BitConverter.ToSingle(binary, 1);
                                        y = BitConverter.ToSingle(binary, 5);
                                    }
                                    else if (opcode == 0x04)
                                    {
                                        // Mouse Left Down
                                        var screenX = (int)(x * width);
                                        var screenY = (int)(y * height);

                                        browserWrapper?.Click(screenX, screenY);
                                    }
                                    else if (opcode == 0x07)
                                    {
                                        // Mouse Left Up
                                    }
                                }
                                catch
                                {
                                }
                            }

                            Log.Logger.Warning("Got metadata: " + metadataConverted);
                            NDIlib.send_free_metadata(senderHandle, ref metadata);
                        }
                    }
                }
                catch (Exception ex) when (!metadataToken.IsCancellationRequested)
                {
                    Log.Warning(ex, "NDI metadata capture thread terminated with an exception");
                }
                finally
                {
                    Log.Information("NDI metadata capture thread exiting");
                }
            })
            {
                IsBackground = true,
                Name = "NDI metadata capture"
            };
            metadataThread.Start();
            metadataThreadStarted = true;


            app.MapPost("/seturl", (HttpContext httpContext, GoToUrlModel url) =>
            {
                browserWrapper.SetUrl(url.Url);
                return true;
            })
            .WithOpenApi();

            app.MapGet("/scroll/{increment}", (int increment) =>
            {
                browserWrapper.ScrollBy(increment);
            }).WithOpenApi();

            app.MapGet("/click/{x}/{y}", (int x, int y) =>
            {
                browserWrapper.Click(x, y);
            }).WithOpenApi();

            app.MapPost("/keystroke", (SendKeystrokeModel model) =>
            {
                browserWrapper.SendKeystrokes(model);
            }).WithOpenApi();

            app.MapGet("/type/{toType}", (string toType) =>
            {
                browserWrapper.SendKeystrokes(new SendKeystrokeModel
                {
                    ToSend = toType
                });
            }).WithOpenApi();

            app.MapGet("/refresh", () =>
            {
                browserWrapper.RefreshPage();
            }).WithOpenApi();

            Log.Information("Starting ASP.NET Core host");
            try
            {
                app.Run();
                Log.Information("ASP.NET Core host exited");
            }
            finally
            {
                Log.Information("ASP.NET Core host shutdown sequence initiated");
            }
        }
        finally
        {
            if (Tractus.HtmlToNdi.Preview.PreviewApp.PreviewWindowInstance is { } previewWin)
            {
                try
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(previewWin.Close);
                }
                catch
                {
                }
            }

            try
            {
                Log.Information("Stopping NDI metadata capture thread");
                metadataCancellation?.Cancel();

                if (metadataThreadStarted && metadataThread is not null)
                {
                    if (!metadataThread.Join(TimeSpan.FromSeconds(5)))
                    {
                        Log.Warning("NDI metadata capture thread did not exit within timeout; waiting indefinitely");
                        metadataThread.Join();
                    }
                }

                Log.Information("NDI metadata capture thread stopped");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to stop metadata capture thread cleanly");
            }
            finally
            {
                metadataCancellation?.Dispose();
            }

            NdiVideoPipeline? pipelineToDispose = null;

            try
            {
                if (browserWrapper is not null)
                {
                    Log.Information("Disposing browser wrapper");
                    browserWrapper.Dispose();
                    pipelineAttachedToBrowser = false;
                    Log.Information("Browser wrapper disposed");
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Browser wrapper disposal encountered an exception");
            }
            finally
            {
                if (pipelineAttachedToBrowser)
                {
                    Log.Warning("Browser wrapper disposal failed; detaching video pipeline to allow cleanup");
                }

                pipelineAttachedToBrowser = false;

                browserWrapper = null!;

                if (Cef.IsInitialized == true)
                {
                    try
                    {
                        Log.Information("Shutting down Cef");
                        Cef.Shutdown();
                        Log.Information("Cef shutdown complete");
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Cef shutdown encountered an exception");
                    }
                }

                if (videoPipeline is not null)
                {
                    pipelineToDispose = videoPipeline;
                    videoPipeline = null;
                }
            }

            try
            {
                if (Directory.Exists(launchCachePath))
                {
                    Log.Information("Deleting launch cache {CachePath}", launchCachePath);
                    Directory.Delete(launchCachePath, true);
                    Log.Information("Launch cache {CachePath} deleted", launchCachePath);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to delete launch cache {CachePath}", launchCachePath);
            }

            if (pipelineToDispose is not null)
            {
                try
                {
                    Log.Information("Disposing NDI video pipeline");
                    pipelineToDispose.Dispose();
                    Log.Information("NDI video pipeline disposed");
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "NDI video pipeline disposal encountered an exception");
                }
            }

            try
            {
                if (Program.NdiSenderPtr != nint.Zero)
                {
                    Log.Information("Destroying NDI sender instance");
                    NDIlib.send_destroy(Program.NdiSenderPtr);
                    Program.NdiSenderPtr = nint.Zero;
                    Log.Information("NDI sender instance destroyed");
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to destroy NDI sender instance");
            }

            try
            {
                Log.Information("Destroying NDI runtime");
                NDIlib.destroy();
                Log.Information("NDI runtime destroyed");
            }
            catch (EntryPointNotFoundException ex)
            {
                Log.Warning(ex, "NDIlib.destroy is not available in this runtime");
            }
            catch (DllNotFoundException ex)
            {
                Log.Warning(ex, "NDIlib.destroy failed because the native library is unavailable");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Unexpected exception while destroying NDI runtime");
            }

            try
            {
                if (NdiNativeLibraryHandle != nint.Zero)
                {
                    Log.Information("Unloading native NDI library");
                    NativeLibrary.Free(NdiNativeLibraryHandle);
                    NdiNativeLibraryHandle = nint.Zero;
                    Log.Information("Native NDI library unloaded");
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to unload native NDI library");
            }

            Log.Information("RunApplication cleanup completed after {Elapsed}ms", StartupStopwatch.ElapsedMilliseconds);
        }
    }

    private static void EnsureNdiNativeLibraryLoaded()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        lock (NdiLibraryLock)
        {
            if (NdiLibraryConfigured)
            {
                return;
            }

            var archSuffix = Environment.Is64BitProcess ? "x64" : "x86";
            var bundledRuntimeDirectory = Path.Combine(AppContext.BaseDirectory, $"runtimes", $"win-{archSuffix}", "native");
            NdiBundledLibraryPath = Path.Combine(bundledRuntimeDirectory, $"Processing.NDI.Lib.{archSuffix}.dll");

            if (File.Exists(NdiBundledLibraryPath))
            {
                NdiSearchDirectories = new[] { bundledRuntimeDirectory };
                NdiLibraryCandidates = new[] { NdiBundledLibraryPath };
                Log.Information("Using bundled NDI native runtime from {Library}", NdiBundledLibraryPath);

                try
                {
                    ConfigureSelectedLibrary(NdiBundledLibraryPath, isBundled: true);
                    return;
                }
                catch (DllNotFoundException ex)
                {
                    Log.Warning(ex, "Failed to load packaged NDI runtime from {Library}; probing system locations", NdiBundledLibraryPath);
                }
                catch (BadImageFormatException ex)
                {
                    Log.Warning(ex, "Packaged NDI runtime at {Library} is incompatible; probing system locations", NdiBundledLibraryPath);
                }
                catch (FileLoadException ex)
                {
                    Log.Warning(ex, "Unable to load packaged NDI runtime from {Library}; probing system locations", NdiBundledLibraryPath);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Unexpected failure loading packaged NDI runtime from {Library}; probing system locations", NdiBundledLibraryPath);
                }
            }
            else
            {
                Log.Information("Bundled NDI native runtime not found at {Library}; probing known locations", NdiBundledLibraryPath);
            }

            NdiSearchDirectories = BuildNdiProbeDirectories();
            NdiLibraryCandidates = BuildNdiCandidateFiles(NdiSearchDirectories);
            Log.Information("NDI probe directories: {Directories}", NdiSearchDirectories);
            Log.Information("NDI library candidates: {Candidates}", NdiLibraryCandidates);

            if (NdiLibraryCandidates.Length == 0)
            {
                throw new DllNotFoundException(CreateNdiFailureMessage());
            }

            static bool IsManagedWrapper(string path)
            {
                var fileName = Path.GetFileName(path);
                return fileName.Contains("DotNet", StringComparison.OrdinalIgnoreCase)
                    || fileName.EndsWith("DotNetCore.dll", StringComparison.OrdinalIgnoreCase)
                    || fileName.EndsWith("DotNetCoreBase.dll", StringComparison.OrdinalIgnoreCase);
            }

            var nativeCandidates = NdiLibraryCandidates
                .Where(path => !IsManagedWrapper(path))
                .ToArray();

            if (nativeCandidates.Length == 0)
            {
                nativeCandidates = NdiLibraryCandidates;
            }

            var preferredNames = new[]
            {
                $"Processing.NDI.Lib.{archSuffix}.dll",
                $"NDIlib.{archSuffix}.dll",
                "NDIlib.dll"
            };

            string? SelectPreferredCandidate(IEnumerable<string> candidates)
            {
                foreach (var preferredName in preferredNames)
                {
                    var match = candidates.FirstOrDefault(path =>
                        string.Equals(Path.GetFileName(path), preferredName, StringComparison.OrdinalIgnoreCase));
                    if (match is not null)
                    {
                        return match;
                    }
                }

                return candidates.FirstOrDefault();
            }

            var remainingCandidates = nativeCandidates.ToList();

            while (remainingCandidates.Count > 0)
            {
                var selectedLibrary = SelectPreferredCandidate(remainingCandidates);

                if (string.IsNullOrWhiteSpace(selectedLibrary))
                {
                    break;
                }

                try
                {
                    ConfigureSelectedLibrary(selectedLibrary, isBundled: false);
                    return;
                }
                catch (DllNotFoundException ex)
                {
                    Log.Warning(ex, "NDI native library {Library} failed to load; will try remaining candidates", selectedLibrary);
                }
                catch (BadImageFormatException ex)
                {
                    Log.Warning(ex, "NDI native library {Library} has incompatible format; will try remaining candidates", selectedLibrary);
                }
                catch (FileLoadException ex)
                {
                    Log.Warning(ex, "NDI native library {Library} could not be loaded; will try remaining candidates", selectedLibrary);
                }

                RemoveCandidate(selectedLibrary);
            }

            throw new DllNotFoundException(CreateNdiFailureMessage());

            void RemoveCandidate(string candidate)
            {
                remainingCandidates.RemoveAll(path => string.Equals(path, candidate, StringComparison.OrdinalIgnoreCase));
                NdiLibraryCandidates = NdiLibraryCandidates
                    .Where(path => !string.Equals(path, candidate, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            }

            void ConfigureSelectedLibrary(string libraryPath, bool isBundled)
            {
                var runtimeDirectory = Path.GetDirectoryName(libraryPath);
                if (string.IsNullOrWhiteSpace(runtimeDirectory))
                {
                    throw new DllNotFoundException(CreateNdiFailureMessage());
                }

                runtimeDirectory = Path.GetFullPath(runtimeDirectory);
                if (isBundled)
                {
                    Log.Information("Using packaged NDI runtime directory {RuntimeDirectory}", runtimeDirectory);
                }
                else
                {
                    Log.Information("Using NDI runtime directory {RuntimeDirectory}", runtimeDirectory);
                }

                Log.Information("Selected NDI native library {Library}", libraryPath);
                Environment.SetEnvironmentVariable("NDILIB_REDIST_FOLDER", runtimeDirectory);

                TryPrependToPath(runtimeDirectory);

                try
                {
                    if (!SetDllDirectory(runtimeDirectory))
                    {
                        var error = Marshal.GetLastWin32Error();
                        Log.Warning("SetDllDirectory failed for {Path} with error {Error}", runtimeDirectory, error);
                    }
                }
                catch (EntryPointNotFoundException)
                {
                    // Older Windows versions may not expose SetDllDirectory; ignore.
                }

                if (NativeLibrary.TryLoad(libraryPath, out var handle))
                {
                    NdiNativeLibraryHandle = handle;
                    Log.Information("Loaded native NDI library from {Library}", libraryPath);
                }
                else
                {
                    Log.Warning("NativeLibrary.TryLoad was unable to load {Library}; relying on default probing.", libraryPath);
                }

                try
                {
                    var versionPtr = NDIlib.version();
                    var version = Marshal.PtrToStringAnsi(versionPtr);
                    if (!string.IsNullOrWhiteSpace(version))
                    {
                        Log.Information("Detected NDI runtime {Version}", version);
                    }
                }
                catch (DllNotFoundException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Unable to query NDI runtime version");
                }

                NdiLibraryConfigured = true;
            }
        }
    }

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetDllDirectory(string lpPathName);

    private static void TryPrependToPath(string directory)
    {
        var current = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var paths = current.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        if (!paths.Any(p => string.Equals(p.Trim(), directory, StringComparison.OrdinalIgnoreCase)))
        {
            var updated = string.IsNullOrEmpty(current)
                ? directory
                : directory + Path.PathSeparator + current;

            Environment.SetEnvironmentVariable("PATH", updated);
        }
    }

    private static string[] BuildNdiProbeDirectories()
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddIfExists(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                var full = Path.GetFullPath(path);
                if (Directory.Exists(full))
                {
                    directories.Add(full);
                }
            }
            catch
            {
                // ignore malformed paths
            }
        }

        AddIfExists(AppContext.BaseDirectory);
        AddIfExists(Path.Combine(AppContext.BaseDirectory, Environment.Is64BitProcess ? "x64" : "x86"));
        AddIfExists(Path.Combine(AppContext.BaseDirectory, "runtimes", Environment.Is64BitProcess ? "win-x64" : "win-x86", "native"));

        var envVars = new[]
        {
            "NDILIB_REDIST_FOLDER",
            "NDI_RUNTIME_DIR",
            "NDI_RUNTIME_DIR_V6",
            "NDI_SDK_DIR",
            "NDI_SDK",
            "NDI_LIB_PATH",
        };

        foreach (var envVar in envVars)
        {
            AddIfExists(Environment.GetEnvironmentVariable(envVar));
        }

        foreach (var root in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        })
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var ndiRoot = Path.Combine(root, "NDI");
            AddIfExists(ndiRoot);

            if (Directory.Exists(ndiRoot))
            {
                try
                {
                    foreach (var sub in Directory.EnumerateDirectories(ndiRoot))
                    {
                        AddIfExists(sub);
                        AddIfExists(Path.Combine(sub, "v6"));
                        AddIfExists(Path.Combine(sub, "Runtime"));
                        AddIfExists(Path.Combine(sub, "Bin"));
                        AddIfExists(Path.Combine(sub, "Bin", Environment.Is64BitProcess ? "x64" : "x86"));
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Failed to enumerate NDI install directories under {Root}", ndiRoot);
                }
            }

            AddIfExists(Path.Combine(root, "NDI", "NDI 6 Runtime", "v6"));
            AddIfExists(Path.Combine(root, "NDI", "NDI 6 SDK"));
            AddIfExists(Path.Combine(root, "NDI", "NDI 6 SDK", "Bin"));
            AddIfExists(Path.Combine(root, "NDI", "NDI 6 SDK", "Bin", Environment.Is64BitProcess ? "x64" : "x86"));
            AddIfExists(Path.Combine(root, "NDI", "NDI 6 Tools", "Runtime"));
        }

        return directories.ToArray();
    }

    private static string[] BuildNdiCandidateFiles(string[] directories)
    {
        var archSuffix = Environment.Is64BitProcess ? "x64" : "x86";
        var fileNames = new[]
        {
            "NDILib.dll",
            $"NDILib.{archSuffix}.dll",
            $"Processing.NDI.Lib.{archSuffix}.dll",
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        void AddCandidate(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                var full = Path.GetFullPath(path);
                if (File.Exists(full) && seen.Add(full))
                {
                    result.Add(full);
                }
            }
            catch
            {
            }
        }

        foreach (var directory in directories)
        {
            foreach (var name in fileNames)
            {
                AddCandidate(Path.Combine(directory, name));
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(directory, $"Processing.NDI.Lib.{archSuffix}.dll", SearchOption.AllDirectories))
                {
                    AddCandidate(file);
                }

                foreach (var file in Directory.EnumerateFiles(directory, "NDILib*.dll", SearchOption.AllDirectories))
                {
                    AddCandidate(file);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to enumerate native NDI libraries under {Directory}", directory);
            }
        }

        // As a final fallback, check for a sibling Processing.NDI.Lib.* next to the executable even if the directory was not captured above.
        foreach (var name in fileNames)
        {
            AddCandidate(Path.Combine(AppContext.BaseDirectory, name));
        }

        return result.ToArray();
    }

    private static string CreateNdiFailureMessage()
    {
        var builder = new StringBuilder();
        builder.Append("Unable to locate the native NDI runtime (expecting Processing.NDI.Lib.*). ");

        if (!string.IsNullOrWhiteSpace(NdiBundledLibraryPath))
        {
            builder.Append("Packaged location: ");
            builder.Append(NdiBundledLibraryPath);
            builder.Append(File.Exists(NdiBundledLibraryPath) ? " (present). " : " (missing). ");
        }

        if (NdiLibraryCandidates.Length > 0)
        {
            builder.Append("Candidate files: ");
            builder.Append(string.Join(", ", NdiLibraryCandidates));
            builder.Append(". ");
        }

        if (NdiSearchDirectories.Length > 0)
        {
            builder.Append("Probed directories: ");
            builder.Append(string.Join(", ", NdiSearchDirectories));
            builder.Append(". ");
        }

        builder.Append("Install the NDI Runtime, or copy Processing.NDI.Lib.x64.dll into runtimes/win-x64/native (or another folder referenced by NDILIB_REDIST_FOLDER), then restart the application.");

        return builder.ToString();
    }

    private static string[] RemoveLauncherFlags(string[] args)
        => args.Where(a => !string.Equals(a, "--launcher", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(a, "--no-launcher", StringComparison.OrdinalIgnoreCase)).ToArray();

    private static bool ShouldUseLauncher(string[] args)
    {
        if (args.Any(a => string.Equals(a, "--launcher", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (args.Any(a => string.Equals(a, "--no-launcher", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (args.Length == 0)
        {
            return true;
        }

        foreach (var arg in args)
        {
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var switchName = arg.Split('=', 2)[0];
            if (ConfigurationSwitches.Contains(switchName))
            {
                return false;
            }
        }

        return true;
    }

    private static readonly HashSet<string> ConfigurationSwitches = new(StringComparer.Ordinal)
    {
        "--ndiname",
        "--port",
        "--url",
        "--w",
        "--h",
        "--fps",
        "--buffer-depth",
        "--telemetry-interval",
        "--allow-latency-expansion",
        "--disable-capture-alignment",
        "--align-with-capture-timestamps",
        "--disable-cadence-telemetry",
        "--enable-cadence-telemetry",
        "--enable-paced-invalidation",
        "--disable-paced-invalidation",
        "--enable-capture-backpressure",
        "--disable-capture-backpressure",
        "--enable-pump-cadence-adaptation",
        "--disable-pump-cadence-adaptation",
        "--windowless-frame-rate",
        "--enable-output-buffer",
        "--disable-gpu-vsync",
        "--disable-frame-rate-limit",
        "--preset-high-performance",
        "--pacing-mode",
        "--ndi-send-async",
    };
}

