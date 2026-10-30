using System;
using Tractus.HtmlToNdi.Launcher;
using Xunit;

namespace Tractus.HtmlToNdi.Tests;

public class LaunchParametersTests
{
    [Fact]
    public void TryFromArgs_ParsesNewCefFlagsCorrectly()
    {
        var args = new[]
        {
            "--ndiname=TestSource",
            "--port=8080",
            "--url=https://example.com/",
            "--force-high-performance-gpu",
            "--enable-begin-frame-scheduling",
            "--disable-new-content-rendering-delay",
            "--cef-extra-args=num-raster-threads=4;disable-gpu-vsync=0"
        };

        var success = LaunchParameters.TryFromArgs(args, out var parameters);

        Assert.True(success);
        Assert.NotNull(parameters);
        Assert.True(parameters.ForceHighPerformanceGpu);
        Assert.True(parameters.EnableBeginFrameScheduling);
        Assert.True(parameters.DisableNewContentRenderingDelay);
        Assert.False(parameters.DisableGpuVsync);
        Assert.False(parameters.DisableFrameRateLimit);
        Assert.Equal("num-raster-threads=4;disable-gpu-vsync=0", parameters.CefExtraArgs);
    }

    [Fact]
    public void FromSettings_MapsNewFieldsCorrectly()
    {
        var settings = new LauncherSettings
        {
            NdiName = "CustomSource",
            Port = 7777,
            Url = "https://example.org/",
            ForceHighPerformanceGpu = true,
            EnableBeginFrameScheduling = true,
            DisableNewContentRenderingDelay = true,
            DisableGpuVsync = false,
            DisableFrameRateLimit = false,
            PresetHighPerformance = false,
            CefExtraArgs = "foo=bar"
        };

        var parameters = LaunchParameters.FromSettings(settings);

        Assert.NotNull(parameters);
        Assert.True(parameters.ForceHighPerformanceGpu);
        Assert.True(parameters.EnableBeginFrameScheduling);
        Assert.True(parameters.DisableNewContentRenderingDelay);
        Assert.False(parameters.DisableGpuVsync);
        Assert.False(parameters.DisableFrameRateLimit);
        Assert.False(parameters.PresetHighPerformance);
        Assert.Equal("foo=bar", parameters.CefExtraArgs);
    }

    [Fact]
    public void LauncherSettings_DefaultsHaveVsyncAndRateLimitActive()
    {
        var settings = new LauncherSettings();

        // Defaults must NOT disable vsync or frame rate limit
        Assert.False(settings.DisableGpuVsync);
        Assert.False(settings.DisableFrameRateLimit);
        Assert.False(settings.PresetHighPerformance);
        Assert.False(settings.ForceHighPerformanceGpu);
        Assert.False(settings.EnableBeginFrameScheduling);
        Assert.False(settings.DisableNewContentRenderingDelay);
        Assert.Null(settings.CefExtraArgs);
    }
}
