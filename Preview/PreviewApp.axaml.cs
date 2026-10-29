using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Serilog;
using Tractus.HtmlToNdi.Chromium;

namespace Tractus.HtmlToNdi.Preview;

public partial class PreviewApp : Avalonia.Application
{
    internal static CefWrapper? PendingWrapper { get; set; }
    public static int PendingWidth { get; set; }
    public static int PendingHeight { get; set; }
    public static ILogger? PendingLogger { get; set; }
    public static BrowserPreviewWindow? PreviewWindowInstance { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new BrowserPreviewWindow(
                PendingWrapper!,
                PendingWidth,
                PendingHeight,
                PendingLogger ?? Log.Logger);

            PreviewWindowInstance = window;
            PendingWrapper?.AttachPreview(window);
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
