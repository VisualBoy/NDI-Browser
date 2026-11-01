using System;
using System.IO;
using System.IO.Compression;
using CefSharp;
using Serilog;
using Tractus.HtmlToNdi.Chromium;
using Xunit;

namespace Tractus.HtmlToNdi.Tests;

public class NdiKvmHandlerTests
{
    private class MockBrowserActionTracker : IBrowserInputDispatcher
    {
        public int LastMouseMoveX { get; set; } = -1;
        public int LastMouseMoveY { get; set; } = -1;
        public bool LastMouseLeave { get; set; }

        public int LastClickX { get; set; } = -1;
        public int LastClickY { get; set; } = -1;
        public MouseButtonType? LastClickButton { get; set; }
        public bool? LastClickMouseUp { get; set; }
        public int LastClickCount { get; set; }

        public int LastWheelX { get; set; } = -1;
        public int LastWheelY { get; set; } = -1;
        public int LastWheelDeltaX { get; set; }
        public int LastWheelDeltaY { get; set; }

        public int MouseMoveCount { get; set; }
        public int MouseClickCount { get; set; }
        public int MouseWheelCount { get; set; }

        public void SendMouseMove(int x, int y, bool mouseLeave)
        {
            this.MouseMoveCount++;
            this.LastMouseMoveX = x;
            this.LastMouseMoveY = y;
            this.LastMouseLeave = mouseLeave;
        }

        public void SendMouseClick(int x, int y, MouseButtonType button, bool mouseUp, int clickCount)
        {
            this.MouseClickCount++;
            this.LastClickX = x;
            this.LastClickY = y;
            this.LastClickButton = button;
            this.LastClickMouseUp = mouseUp;
            this.LastClickCount = clickCount;
        }

        public void SendMouseWheel(int x, int y, int deltaX, int deltaY)
        {
            this.MouseWheelCount++;
            this.LastWheelX = x;
            this.LastWheelY = y;
            this.LastWheelDeltaX = deltaX;
            this.LastWheelDeltaY = deltaY;
        }
    }

    [Fact]
    public void ExtractPayload_Uncompressed_ExtractsValidBytes()
    {
        var xml = "<ndi_kvm u=\"Ayj5ET8buJw+AQ==\"/>";
        var success = NdiKvmHandler.ExtractPayload(xml, out var payload);

        Assert.True(success);
        Assert.NotNull(payload);
        Assert.Equal(10, payload!.Length);
        Assert.Equal(0x03, payload[0]);
    }

    [Fact]
    public void ExtractPayload_Compressed_DecompressesCorrectly()
    {
        var original = new byte[] { 0x04 };
        byte[] compressed;
        using (var ms = new MemoryStream())
        {
            using (var zs = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            {
                zs.Write(original, 0, original.Length);
            }
            compressed = ms.ToArray();
        }

        var b64 = Convert.ToBase64String(compressed);
        var xml = $"<ndi_kvm d=\"{b64}\"/>";

        var success = NdiKvmHandler.ExtractPayload(xml, out var payload);
        Assert.True(success);
        Assert.NotNull(payload);
        Assert.Equal(original, payload);
    }

    [Fact]
    public void ExtractPayload_InvalidXml_ReturnsFalse()
    {
        Assert.False(NdiKvmHandler.ExtractPayload("", out _));
        Assert.False(NdiKvmHandler.ExtractPayload("<other_element />", out _));
        Assert.False(NdiKvmHandler.ExtractPayload("<ndi_kvm u=\"not-valid-base-64!\"/>", out _));
    }

    [Fact]
    public void ProcessBinaryPayload_MouseMove_UpdatesCoordinatesAndDispatches()
    {
        var tracker = new MockBrowserActionTracker();
        int width = 1920;
        int height = 1080;

        var handler = new NdiKvmHandler(
            () => tracker,
            () => width,
            () => height,
            Log.Logger);

        // Position: x = 0.5f, y = 0.25f -> screen (960, 270)
        var payload = new byte[9];
        payload[0] = NdiKvmHandler.OpcodeMouseMove;
        BitConverter.GetBytes(0.5f).CopyTo(payload, 1);
        BitConverter.GetBytes(0.25f).CopyTo(payload, 5);

        var handled = handler.ProcessBinaryPayload(payload);
        Assert.True(handled);
        Assert.Equal(0.5f, handler.CurrentX);
        Assert.Equal(0.25f, handler.CurrentY);
        Assert.Equal(1, tracker.MouseMoveCount);
        Assert.Equal(960, tracker.LastMouseMoveX);
        Assert.Equal(270, tracker.LastMouseMoveY);
        Assert.False(tracker.LastMouseLeave);
    }

    [Fact]
    public void ProcessBinaryPayload_ClickAndDragSequence_MaintainsCoordinates()
    {
        var tracker = new MockBrowserActionTracker();
        int width = 1000;
        int height = 1000;

        var handler = new NdiKvmHandler(
            () => tracker,
            () => width,
            () => height,
            Log.Logger);

        // 1. Move to start (0.1, 0.1) -> (100, 100)
        var move1 = new byte[9];
        move1[0] = NdiKvmHandler.OpcodeMouseMove;
        BitConverter.GetBytes(0.1f).CopyTo(move1, 1);
        BitConverter.GetBytes(0.1f).CopyTo(move1, 5);
        handler.ProcessBinaryPayload(move1);

        Assert.Equal(100, tracker.LastMouseMoveX);
        Assert.Equal(100, tracker.LastMouseMoveY);

        // 2. Left Down (0x04)
        Assert.True(handler.ProcessBinaryPayload(new byte[] { NdiKvmHandler.OpcodeLeftDown }));
        Assert.Equal(MouseButtonType.Left, tracker.LastClickButton);
        Assert.False(tracker.LastClickMouseUp);
        Assert.Equal(100, tracker.LastClickX);
        Assert.Equal(100, tracker.LastClickY);

        // 3. Drag to (0.5, 0.8) -> (500, 800)
        var move2 = new byte[9];
        move2[0] = NdiKvmHandler.OpcodeMouseMove;
        BitConverter.GetBytes(0.5f).CopyTo(move2, 1);
        BitConverter.GetBytes(0.8f).CopyTo(move2, 5);
        handler.ProcessBinaryPayload(move2);

        Assert.Equal(500, tracker.LastMouseMoveX);
        Assert.Equal(800, tracker.LastMouseMoveY);

        // 4. Left Up (0x07)
        Assert.True(handler.ProcessBinaryPayload(new byte[] { NdiKvmHandler.OpcodeLeftUp }));
        Assert.Equal(MouseButtonType.Left, tracker.LastClickButton);
        Assert.True(tracker.LastClickMouseUp);
        Assert.Equal(500, tracker.LastClickX);
        Assert.Equal(800, tracker.LastClickY);
    }

    [Fact]
    public void ProcessBinaryPayload_AllButtonOpcodes_AreDispatchedCorrectly()
    {
        var tracker = new MockBrowserActionTracker();
        var handler = new NdiKvmHandler(
            () => tracker,
            () => 1920,
            () => 1080,
            Log.Logger);

        // Left Down / Up
        Assert.True(handler.ProcessBinaryPayload(new byte[] { NdiKvmHandler.OpcodeLeftDown }));
        Assert.Equal(MouseButtonType.Left, tracker.LastClickButton);
        Assert.False(tracker.LastClickMouseUp);

        Assert.True(handler.ProcessBinaryPayload(new byte[] { NdiKvmHandler.OpcodeLeftUp }));
        Assert.Equal(MouseButtonType.Left, tracker.LastClickButton);
        Assert.True(tracker.LastClickMouseUp);

        // Middle Down / Up
        Assert.True(handler.ProcessBinaryPayload(new byte[] { NdiKvmHandler.OpcodeMiddleDown }));
        Assert.Equal(MouseButtonType.Middle, tracker.LastClickButton);
        Assert.False(tracker.LastClickMouseUp);

        Assert.True(handler.ProcessBinaryPayload(new byte[] { NdiKvmHandler.OpcodeMiddleUp }));
        Assert.Equal(MouseButtonType.Middle, tracker.LastClickButton);
        Assert.True(tracker.LastClickMouseUp);

        // Right Down / Up
        Assert.True(handler.ProcessBinaryPayload(new byte[] { NdiKvmHandler.OpcodeRightDown }));
        Assert.Equal(MouseButtonType.Right, tracker.LastClickButton);
        Assert.False(tracker.LastClickMouseUp);

        Assert.True(handler.ProcessBinaryPayload(new byte[] { NdiKvmHandler.OpcodeRightUp }));
        Assert.Equal(MouseButtonType.Right, tracker.LastClickButton);
        Assert.True(tracker.LastClickMouseUp);

        // Wheel payload: 0x0A followed by 4-byte float (-4.0f)
        var wheelPayload = new byte[5];
        wheelPayload[0] = NdiKvmHandler.OpcodeMouseWheel;
        BitConverter.GetBytes(-4.0f).CopyTo(wheelPayload, 1);
        Assert.True(handler.ProcessBinaryPayload(wheelPayload));
        Assert.Equal(-480, tracker.LastWheelDeltaY); // -4.0 * 120

        // Unknown opcode returns false
        Assert.False(handler.ProcessBinaryPayload(new byte[] { 0xFF }));
    }
}
