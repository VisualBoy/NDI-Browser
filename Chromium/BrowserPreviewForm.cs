using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
using CefSharp;
using Serilog;

namespace Tractus.HtmlToNdi.Chromium;

/// <summary>
/// Provides an interactive desktop preview window that renders the off-screen browser output
/// and forwards mouse and keyboard user interactions into Chromium.
/// </summary>
public sealed class BrowserPreviewForm : Form, IBrowserPreviewWindow
{
    private readonly CefWrapper wrapper;
    private readonly ILogger logger;
    private readonly object bufferLock = new();

    private byte[]? frameBuffer;
    private int bufferWidth;
    private int bufferHeight;
    private Bitmap? previewBitmap;
    private bool framePending;
    private bool disposedValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="BrowserPreviewForm"/> class.
    /// </summary>
    /// <param name="wrapper">The CefWrapper instance to display and interact with.</param>
    /// <param name="browserWidth">The initial width of the browser canvas.</param>
    /// <param name="browserHeight">The initial height of the browser canvas.</param>
    /// <param name="logger">The logger instance.</param>
    internal BrowserPreviewForm(CefWrapper wrapper, int browserWidth, int browserHeight, ILogger logger)
    {
        this.wrapper = wrapper ?? throw new ArgumentNullException(nameof(wrapper));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));

        this.Text = $"NDI Browser - Interactive Preview ({browserWidth}x{browserHeight})";
        this.DoubleBuffered = true;
        this.SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.Opaque,
            true);

        // Limit initial window size so it doesn't exceed screen working area on high-res displays
        var screenArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        var initialW = Math.Min(browserWidth, (int)(screenArea.Width * 0.85));
        var initialH = Math.Min(browserHeight, (int)(screenArea.Height * 0.85));

        this.ClientSize = new Size(Math.Max(initialW, 640), Math.Max(initialH, 360));
        this.StartPosition = FormStartPosition.CenterScreen;
        this.MinimumSize = new Size(320, 240);

        this.MouseMove += this.OnPreviewMouseMove;
        this.MouseDown += this.OnPreviewMouseDown;
        this.MouseUp += this.OnPreviewMouseUp;
        this.MouseWheel += this.OnPreviewMouseWheel;
        this.KeyDown += this.OnPreviewKeyDown;
        this.KeyUp += this.OnPreviewKeyUp;
        this.KeyPress += this.OnPreviewKeyPress;
    }

    /// <summary>
    /// Updates the preview buffer with raw BGRA pixel data from the renderer.
    /// Thread-safe and callable from CEF rendering threads.
    /// </summary>
    /// <param name="bufferHandle">Pointer to the unmanaged BGRA buffer.</param>
    /// <param name="width">Width of the rendered frame.</param>
    /// <param name="height">Height of the rendered frame.</param>
    public void UpdateFrame(nint bufferHandle, int width, int height)
    {
        if (this.disposedValue || bufferHandle == nint.Zero || width <= 0 || height <= 0)
        {
            return;
        }

        var byteCount = width * height * 4;

        lock (this.bufferLock)
        {
            if (this.frameBuffer is null || this.frameBuffer.Length != byteCount || this.bufferWidth != width || this.bufferHeight != height)
            {
                this.frameBuffer = new byte[byteCount];
                this.bufferWidth = width;
                this.bufferHeight = height;
            }

            unsafe
            {
                fixed (byte* pDest = this.frameBuffer)
                {
                    Buffer.MemoryCopy((void*)bufferHandle, pDest, byteCount, byteCount);
                }
            }

            this.framePending = true;
        }

        if (this.IsHandleCreated && !this.disposedValue)
        {
            try
            {
                this.BeginInvoke((Action)this.Invalidate);
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    /// <inheritdoc/>
    protected override void OnPaint(PaintEventArgs e)
    {
        if (this.disposedValue)
        {
            return;
        }

        lock (this.bufferLock)
        {
            if (this.frameBuffer is not null && this.bufferWidth > 0 && this.bufferHeight > 0)
            {
                if (this.previewBitmap is null ||
                    this.previewBitmap.Width != this.bufferWidth ||
                    this.previewBitmap.Height != this.bufferHeight)
                {
                    this.previewBitmap?.Dispose();
                    this.previewBitmap = new Bitmap(
                        this.bufferWidth,
                        this.bufferHeight,
                        PixelFormat.Format32bppArgb);
                }

                if (this.framePending)
                {
                    var bmpData = this.previewBitmap.LockBits(
                        new Rectangle(0, 0, this.bufferWidth, this.bufferHeight),
                        ImageLockMode.WriteOnly,
                        PixelFormat.Format32bppArgb);

                    unsafe
                    {
                        fixed (byte* pSrc = this.frameBuffer)
                        {
                            Buffer.MemoryCopy(pSrc, (void*)bmpData.Scan0, this.frameBuffer.Length, this.frameBuffer.Length);
                        }
                    }

                    this.previewBitmap.UnlockBits(bmpData);
                    this.framePending = false;
                }

                e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
                e.Graphics.DrawImage(this.previewBitmap, this.ClientRectangle);
                return;
            }
        }

        e.Graphics.Clear(Color.Black);
    }

    private (int X, int Y) TranslateToBrowserCoordinates(int clientX, int clientY)
    {
        if (this.ClientSize.Width <= 0 || this.ClientSize.Height <= 0)
        {
            return (0, 0);
        }

        var browserW = this.wrapper.Width;
        var browserH = this.wrapper.Height;

        var x = (int)((double)clientX * browserW / this.ClientSize.Width);
        var y = (int)((double)clientY * browserH / this.ClientSize.Height);

        return (Math.Clamp(x, 0, browserW - 1), Math.Clamp(y, 0, browserH - 1));
    }

    private static CefEventFlags GetCefModifiers(KeyEventArgs? e)
    {
        var flags = CefEventFlags.None;
        if (Control.ModifierKeys.HasFlag(Keys.Shift)) flags |= CefEventFlags.ShiftDown;
        if (Control.ModifierKeys.HasFlag(Keys.Control)) flags |= CefEventFlags.ControlDown;
        if (Control.ModifierKeys.HasFlag(Keys.Alt)) flags |= CefEventFlags.AltDown;
        return flags;
    }

    private void OnPreviewMouseMove(object? sender, MouseEventArgs e)
    {
        var (x, y) = this.TranslateToBrowserCoordinates(e.X, e.Y);
        this.wrapper.SendMouseMove(x, y, mouseLeave: false);
    }

    private void OnPreviewMouseDown(object? sender, MouseEventArgs e)
    {
        var (x, y) = this.TranslateToBrowserCoordinates(e.X, e.Y);
        var button = e.Button switch
        {
            MouseButtons.Right => MouseButtonType.Right,
            MouseButtons.Middle => MouseButtonType.Middle,
            _ => MouseButtonType.Left,
        };
        this.wrapper.SendMouseClick(x, y, button, mouseUp: false, clickCount: e.Clicks);
    }

    private void OnPreviewMouseUp(object? sender, MouseEventArgs e)
    {
        var (x, y) = this.TranslateToBrowserCoordinates(e.X, e.Y);
        var button = e.Button switch
        {
            MouseButtons.Right => MouseButtonType.Right,
            MouseButtons.Middle => MouseButtonType.Middle,
            _ => MouseButtonType.Left,
        };
        this.wrapper.SendMouseClick(x, y, button, mouseUp: true, clickCount: e.Clicks);
    }

    private void OnPreviewMouseWheel(object? sender, MouseEventArgs e)
    {
        var (x, y) = this.TranslateToBrowserCoordinates(e.X, e.Y);
        this.wrapper.SendMouseWheel(x, y, deltaX: 0, deltaY: e.Delta);
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        this.wrapper.SendKeyEvent(new KeyEvent
        {
            Type = KeyEventType.RawKeyDown,
            WindowsKeyCode = (int)e.KeyCode,
            NativeKeyCode = (int)e.KeyValue,
            IsSystemKey = e.Alt,
            Modifiers = GetCefModifiers(e),
        });
    }

    private void OnPreviewKeyUp(object? sender, KeyEventArgs e)
    {
        this.wrapper.SendKeyEvent(new KeyEvent
        {
            Type = KeyEventType.KeyUp,
            WindowsKeyCode = (int)e.KeyCode,
            NativeKeyCode = (int)e.KeyValue,
            IsSystemKey = e.Alt,
            Modifiers = GetCefModifiers(e),
        });
    }

    private void OnPreviewKeyPress(object? sender, KeyPressEventArgs e)
    {
        this.wrapper.SendKeyEvent(new KeyEvent
        {
            Type = KeyEventType.Char,
            WindowsKeyCode = e.KeyChar,
            NativeKeyCode = e.KeyChar,
        });
    }

    /// <inheritdoc/>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        this.logger.Information("Interactive preview window closing; NDI stream continues");
        this.wrapper.DetachPreviewForm();
        base.OnFormClosing(e);
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (!this.disposedValue)
        {
            if (disposing)
            {
                lock (this.bufferLock)
                {
                    this.previewBitmap?.Dispose();
                    this.previewBitmap = null;
                }
            }

            this.disposedValue = true;
        }

        base.Dispose(disposing);
    }
}
