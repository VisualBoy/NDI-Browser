using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CefSharp;
using Tractus.HtmlToNdi.Chromium;

namespace Tractus.HtmlToNdi.Preview;

public class BrowserViewControl : Avalonia.Controls.Control
{
    private CefWrapper? wrapper;
    private readonly object bufferLock = new();
    private byte[]? frameBuffer;
    private int bufferWidth;
    private int bufferHeight;
    private WriteableBitmap? previewBitmap;
    private bool framePending;
    private bool isDisposed;

    public BrowserViewControl()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    internal void AttachWrapper(CefWrapper cefWrapper)
    {
        this.wrapper = cefWrapper ?? throw new ArgumentNullException(nameof(cefWrapper));
    }

    public void UpdateFrame(nint bufferHandle, int width, int height)
    {
        if (this.isDisposed || bufferHandle == nint.Zero || width <= 0 || height <= 0)
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

        Dispatcher.UIThread.Post(this.InvalidateVisual, DispatcherPriority.Render);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (this.isDisposed)
        {
            return;
        }

        var bounds = this.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        lock (this.bufferLock)
        {
            if (this.frameBuffer is not null && this.bufferWidth > 0 && this.bufferHeight > 0)
            {
                if (this.previewBitmap is null ||
                    this.previewBitmap.PixelSize.Width != this.bufferWidth ||
                    this.previewBitmap.PixelSize.Height != this.bufferHeight)
                {
                    this.previewBitmap?.Dispose();
                    this.previewBitmap = new WriteableBitmap(
                        new PixelSize(this.bufferWidth, this.bufferHeight),
                        new Vector(96, 96),
                        Avalonia.Platform.PixelFormat.Bgra8888,
                        AlphaFormat.Premul);
                }

                if (this.framePending)
                {
                    using (var frameLock = this.previewBitmap.Lock())
                    {
                        unsafe
                        {
                            fixed (byte* pSrc = this.frameBuffer)
                            {
                                Buffer.MemoryCopy(pSrc, (void*)frameLock.Address, this.frameBuffer.Length, this.frameBuffer.Length);
                            }
                        }
                    }

                    this.framePending = false;
                }

                context.DrawImage(this.previewBitmap, new Rect(0, 0, bounds.Width, bounds.Height));
                return;
            }
        }

        context.FillRectangle(new SolidColorBrush(Avalonia.Media.Color.FromRgb(18, 18, 18)), new Rect(0, 0, bounds.Width, bounds.Height));
    }

    private (int X, int Y) TranslateToBrowserCoordinates(Avalonia.Point clientPt)
    {
        if (this.wrapper is null || this.Bounds.Width <= 0 || this.Bounds.Height <= 0)
        {
            return (0, 0);
        }

        var browserW = this.wrapper.Width;
        var browserH = this.wrapper.Height;

        var x = (int)(clientPt.X * browserW / this.Bounds.Width);
        var y = (int)(clientPt.Y * browserH / this.Bounds.Height);

        return (Math.Clamp(x, 0, browserW - 1), Math.Clamp(y, 0, browserH - 1));
    }

    private static CefEventFlags GetCefModifiers(KeyModifiers modifiers)
    {
        var flags = CefEventFlags.None;
        if (modifiers.HasFlag(KeyModifiers.Shift)) flags |= CefEventFlags.ShiftDown;
        if (modifiers.HasFlag(KeyModifiers.Control)) flags |= CefEventFlags.ControlDown;
        if (modifiers.HasFlag(KeyModifiers.Alt)) flags |= CefEventFlags.AltDown;
        return flags;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (this.wrapper is null) return;

        var pt = e.GetPosition(this);
        var (x, y) = this.TranslateToBrowserCoordinates(pt);
        this.wrapper.SendMouseMove(x, y, mouseLeave: false);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (this.wrapper is null) return;

        this.Focus();

        var point = e.GetCurrentPoint(this);
        var (x, y) = this.TranslateToBrowserCoordinates(point.Position);

        var button = point.Properties.IsRightButtonPressed ? MouseButtonType.Right :
                     point.Properties.IsMiddleButtonPressed ? MouseButtonType.Middle :
                     MouseButtonType.Left;

        this.wrapper.SendMouseClick(x, y, button, mouseUp: false, clickCount: e.ClickCount);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (this.wrapper is null) return;

        var point = e.GetCurrentPoint(this);
        var (x, y) = this.TranslateToBrowserCoordinates(point.Position);

        var button = e.InitialPressMouseButton switch
        {
            MouseButton.Right => MouseButtonType.Right,
            MouseButton.Middle => MouseButtonType.Middle,
            _ => MouseButtonType.Left,
        };

        this.wrapper.SendMouseClick(x, y, button, mouseUp: true, clickCount: 1);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (this.wrapper is null) return;

        var pt = e.GetPosition(this);
        var (x, y) = this.TranslateToBrowserCoordinates(pt);
        var delta = (int)(e.Delta.Y * 120);
        this.wrapper.SendMouseWheel(x, y, deltaX: 0, deltaY: delta);
    }

    protected override void OnKeyDown(Avalonia.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (this.wrapper is null) return;

        var vk = KeyToVirtualKey(e.Key);
        this.wrapper.SendKeyEvent(new KeyEvent
        {
            Type = KeyEventType.RawKeyDown,
            WindowsKeyCode = vk,
            NativeKeyCode = vk,
            IsSystemKey = e.KeyModifiers.HasFlag(KeyModifiers.Alt),
            Modifiers = GetCefModifiers(e.KeyModifiers),
        });
        e.Handled = true;
    }

    protected override void OnKeyUp(Avalonia.Input.KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (this.wrapper is null) return;

        var vk = KeyToVirtualKey(e.Key);
        this.wrapper.SendKeyEvent(new KeyEvent
        {
            Type = KeyEventType.KeyUp,
            WindowsKeyCode = vk,
            NativeKeyCode = vk,
            IsSystemKey = e.KeyModifiers.HasFlag(KeyModifiers.Alt),
            Modifiers = GetCefModifiers(e.KeyModifiers),
        });
        e.Handled = true;
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (this.wrapper is null || string.IsNullOrEmpty(e.Text)) return;

        foreach (var c in e.Text)
        {
            this.wrapper.SendKeyEvent(new KeyEvent
            {
                Type = KeyEventType.Char,
                WindowsKeyCode = c,
                NativeKeyCode = c,
            });
        }
        e.Handled = true;
    }

    private static int KeyToVirtualKey(Key key)
    {
        // Common key mappings between Avalonia.Input.Key and Windows Virtual-Key codes
        return key switch
        {
            Key.Back => 0x08,
            Key.Tab => 0x09,
            Key.Enter or Key.Return => 0x0D,
            Key.Escape => 0x1B,
            Key.Space => 0x20,
            Key.PageUp => 0x21,
            Key.PageDown => 0x22,
            Key.End => 0x23,
            Key.Home => 0x24,
            Key.Left => 0x25,
            Key.Up => 0x26,
            Key.Right => 0x27,
            Key.Down => 0x28,
            Key.Insert => 0x2D,
            Key.Delete => 0x2E,
            >= Key.D0 and <= Key.D9 => 0x30 + (key - Key.D0),
            >= Key.A and <= Key.Z => 0x41 + (key - Key.A),
            >= Key.NumPad0 and <= Key.NumPad9 => 0x60 + (key - Key.NumPad0),
            Key.Multiply => 0x6A,
            Key.Add => 0x6B,
            Key.Subtract => 0x6D,
            Key.Decimal => 0x6E,
            Key.Divide => 0x6F,
            >= Key.F1 and <= Key.F12 => 0x70 + (key - Key.F1),
            _ => (int)key,
        };
    }

    public void Dispose()
    {
        if (!this.isDisposed)
        {
            this.isDisposed = true;
            lock (this.bufferLock)
            {
                this.previewBitmap?.Dispose();
                this.previewBitmap = null;
                this.frameBuffer = null;
            }
        }
    }
}
