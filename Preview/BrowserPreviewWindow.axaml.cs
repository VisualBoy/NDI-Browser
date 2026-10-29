using System;
using Avalonia.Controls;
using Button = Avalonia.Controls.Button;
using TextBox = Avalonia.Controls.TextBox;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Serilog;
using Tractus.HtmlToNdi.Chromium;

namespace Tractus.HtmlToNdi.Preview;

public partial class BrowserPreviewWindow : Window, IBrowserPreviewWindow
{
    private readonly CefWrapper wrapper;
    private readonly int browserWidth;
    private readonly int browserHeight;
    private readonly ILogger logger;
    private string currentPageTitle = string.Empty;
    private bool disposedValue;

    public BrowserPreviewWindow()
    {
        InitializeComponent();
        this.wrapper = null!;
        this.logger = Log.Logger;
    }

    internal BrowserPreviewWindow(CefWrapper wrapper, int browserWidth, int browserHeight, ILogger logger)
    {
        InitializeComponent();

        this.wrapper = wrapper ?? throw new ArgumentNullException(nameof(wrapper));
        this.browserWidth = browserWidth;
        this.browserHeight = browserHeight;
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));

        this.BrowserView ??= this.FindControl<BrowserViewControl>("BrowserView")!;
        this.AddressTextBox ??= this.FindControl<TextBox>("AddressTextBox")!;
        this.TitleTextBlock ??= this.FindControl<TextBlock>("TitleTextBlock");
        this.MaximizeButton ??= this.FindControl<Button>("MaximizeButton")!;
        this.BackButton ??= this.FindControl<Button>("BackButton");
        this.ForwardButton ??= this.FindControl<Button>("ForwardButton");
        this.ReloadButton ??= this.FindControl<Button>("ReloadButton");
        this.CloseButton ??= this.FindControl<Button>("CloseButton");
        this.MinimizeButton ??= this.FindControl<Button>("MinimizeButton");

        this.BrowserView?.AttachWrapper(wrapper);

        this.UpdateWindowTitle(null);

        if (!string.IsNullOrWhiteSpace(wrapper.Url))
        {
            this.AddressTextBox.Text = wrapper.Url;
        }

        this.wrapper.AddressChanged += this.OnBrowserAddressChanged;
        this.wrapper.TitleChanged += this.OnBrowserTitleChanged;
        this.wrapper.NavigationStateChanged += this.OnBrowserNavigationStateChanged;

        this.PropertyChanged += (s, e) =>
        {
            if (e.Property == WindowStateProperty)
            {
                this.MaximizeButton.Content = this.WindowState == WindowState.Maximized ? "🗗" : "🗖";
                this.MaximizeButton.SetValue(Avalonia.Controls.ToolTip.TipProperty, this.WindowState == WindowState.Maximized ? "Restore" : "Maximize");
            }
        };

        this.Closed += this.OnWindowClosed;
    }


    private void UpdateWindowTitle(string? pageTitle)
    {
        this.currentPageTitle = pageTitle ?? string.Empty;
        var displayTitle = string.IsNullOrWhiteSpace(this.currentPageTitle)
            ? $"NDI Browser ({this.browserWidth}x{this.browserHeight})"
            : $"{this.currentPageTitle} - NDI Browser ({this.browserWidth}x{this.browserHeight})";

        Dispatcher.UIThread.Post(() =>
        {
            this.Title = displayTitle;
            if (this.TitleTextBlock is not null)
            {
                this.TitleTextBlock.Text = displayTitle;
            }
        });
    }

    private void OnBrowserAddressChanged(string address)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!this.AddressTextBox.IsFocused)
            {
                this.AddressTextBox.Text = address;
            }
        });
    }

    private void OnBrowserTitleChanged(string title)
    {
        this.UpdateWindowTitle(title);
    }

    private void OnBrowserNavigationStateChanged(bool canGoBack, bool canGoForward)
    {
        Dispatcher.UIThread.Post(() =>
        {
            this.BackButton.IsEnabled = canGoBack;
            this.ForwardButton.IsEnabled = canGoForward;
        });
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            this.BeginMoveDrag(e);
        }
    }

    private void OnMinimizeClicked(object? sender, RoutedEventArgs e)
    {
        this.WindowState = WindowState.Minimized;
    }

    private void OnMaximizeClicked(object? sender, RoutedEventArgs e)
    {
        this.WindowState = this.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e)
    {
        this.Close();
    }

    private void OnBackClicked(object? sender, RoutedEventArgs e)
    {
        this.wrapper.GoBack();
    }

    private void OnForwardClicked(object? sender, RoutedEventArgs e)
    {
        this.wrapper.GoForward();
    }

    private void OnReloadClicked(object? sender, RoutedEventArgs e)
    {
        this.wrapper.RefreshPage();
    }

    private void AddressTextBox_KeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var url = this.AddressTextBox.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(url))
            {
                if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                    !url.StartsWith("file://", StringComparison.OrdinalIgnoreCase) &&
                    !url.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
                {
                    url = "https://" + url;
                    this.AddressTextBox.Text = url;
                }

                this.wrapper.SetUrl(url);
            }

            e.Handled = true;
        }
    }

    private void AddressTextBox_GotFocus(object? sender, GotFocusEventArgs e)
    {
        this.AddressTextBox.SelectAll();
    }

    public void UpdateFrame(nint bufferHandle, int width, int height)
    {
        if (this.disposedValue) return;
        this.BrowserView?.UpdateFrame(bufferHandle, width, height);
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        this.logger.Information("Interactive preview window closed; NDI stream continues");
        this.wrapper.DetachPreview();
        this.Dispose();
    }

    public void Dispose()
    {
        if (!this.disposedValue)
        {
            this.disposedValue = true;
            this.wrapper.AddressChanged -= this.OnBrowserAddressChanged;
            this.wrapper.TitleChanged -= this.OnBrowserTitleChanged;
            this.wrapper.NavigationStateChanged -= this.OnBrowserNavigationStateChanged;
            this.BrowserView?.Dispose();
        }
    }
}
