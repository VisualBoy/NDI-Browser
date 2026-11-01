using System;
using System.IO;
using System.IO.Compression;
using CefSharp;
using Serilog;

namespace Tractus.HtmlToNdi.Chromium;

/// <summary>
/// Abstraction for dispatching input events to the browser.
/// </summary>
public interface IBrowserInputDispatcher
{
    void SendMouseMove(int x, int y, bool mouseLeave);
    void SendMouseClick(int x, int y, MouseButtonType button, bool mouseUp, int clickCount);
    void SendMouseWheel(int x, int y, int deltaX, int deltaY);
}

/// <summary>
/// Handles incoming NDI KVM metadata frames, decoding binary opcodes and dispatching
/// mouse movement, drag, click, and wheel events to Chromium.
/// </summary>
public class NdiKvmHandler
{
    private readonly Func<IBrowserInputDispatcher?> getDispatcher;
    private readonly Func<int> getWidth;
    private readonly Func<int> getHeight;
    private readonly ILogger logger;

    private float currentX;
    private float currentY;

    // Opcodes per NDI KVM specification
    public const byte OpcodeMouseMove = 0x03;
    public const byte OpcodeLeftDown = 0x04;
    public const byte OpcodeMiddleDown = 0x05;
    public const byte OpcodeRightDown = 0x06;
    public const byte OpcodeLeftUp = 0x07;
    public const byte OpcodeMiddleUp = 0x08;
    public const byte OpcodeRightUp = 0x09;
    public const byte OpcodeMouseWheel = 0x0A;

    /// <summary>
    /// Gets the current normalized X position (0.0 - 1.0).
    /// </summary>
    public float CurrentX => this.currentX;

    /// <summary>
    /// Gets the current normalized Y position (0.0 - 1.0).
    /// </summary>
    public float CurrentY => this.currentY;

    /// <summary>
    /// Initializes a new instance of the <see cref="NdiKvmHandler"/> class.
    /// </summary>
    /// <param name="getDispatcher">Function returning the active browser input dispatcher.</param>
    /// <param name="getWidth">Function returning the target screen width in pixels.</param>
    /// <param name="getHeight">Function returning the target screen height in pixels.</param>
    /// <param name="logger">The logger instance.</param>
    public NdiKvmHandler(Func<IBrowserInputDispatcher?> getDispatcher, Func<int> getWidth, Func<int> getHeight, ILogger logger)
    {
        this.getDispatcher = getDispatcher ?? throw new ArgumentNullException(nameof(getDispatcher));
        this.getWidth = getWidth ?? throw new ArgumentNullException(nameof(getWidth));
        this.getHeight = getHeight ?? throw new ArgumentNullException(nameof(getHeight));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Processes an XML metadata string from NDI, extracting and executing KVM commands if present.
    /// </summary>
    /// <param name="metadataXml">The incoming metadata XML string.</param>
    /// <returns><c>true</c> if a KVM message was recognized and processed; otherwise <c>false</c>.</returns>
    public bool ProcessMetadata(string metadataXml)
    {
        if (string.IsNullOrWhiteSpace(metadataXml))
        {
            return false;
        }

        if (!ExtractPayload(metadataXml, out var binaryPayload) || binaryPayload is null || binaryPayload.Length == 0)
        {
            return false;
        }

        return ProcessBinaryPayload(binaryPayload);
    }

    /// <summary>
    /// Processes a decoded binary KVM payload.
    /// </summary>
    /// <param name="binary">The decoded binary payload.</param>
    /// <returns><c>true</c> if handled; otherwise <c>false</c>.</returns>
    public bool ProcessBinaryPayload(byte[] binary)
    {
        if (binary == null || binary.Length == 0)
        {
            return false;
        }

        var opcode = binary[0];
        var width = this.getWidth();
        var height = this.getHeight();
        var dispatcher = this.getDispatcher();

        var screenX = (int)Math.Clamp(Math.Round(this.currentX * width), 0, Math.Max(0, width - 1));
        var screenY = (int)Math.Clamp(Math.Round(this.currentY * height), 0, Math.Max(0, height - 1));

        switch (opcode)
        {
            case OpcodeMouseMove:
                if (binary.Length >= 9)
                {
                    this.currentX = BitConverter.ToSingle(binary, 1);
                    this.currentY = BitConverter.ToSingle(binary, 5);

                    screenX = (int)Math.Clamp(Math.Round(this.currentX * width), 0, Math.Max(0, width - 1));
                    screenY = (int)Math.Clamp(Math.Round(this.currentY * height), 0, Math.Max(0, height - 1));

                    dispatcher?.SendMouseMove(screenX, screenY, mouseLeave: false);
                    return true;
                }
                break;

            case OpcodeLeftDown:
                dispatcher?.SendMouseClick(screenX, screenY, MouseButtonType.Left, mouseUp: false, clickCount: 1);
                return true;

            case OpcodeLeftUp:
                dispatcher?.SendMouseClick(screenX, screenY, MouseButtonType.Left, mouseUp: true, clickCount: 1);
                return true;

            case OpcodeMiddleDown:
                dispatcher?.SendMouseClick(screenX, screenY, MouseButtonType.Middle, mouseUp: false, clickCount: 1);
                return true;

            case OpcodeMiddleUp:
                dispatcher?.SendMouseClick(screenX, screenY, MouseButtonType.Middle, mouseUp: true, clickCount: 1);
                return true;

            case OpcodeRightDown:
                dispatcher?.SendMouseClick(screenX, screenY, MouseButtonType.Right, mouseUp: false, clickCount: 1);
                return true;

            case OpcodeRightUp:
                dispatcher?.SendMouseClick(screenX, screenY, MouseButtonType.Right, mouseUp: true, clickCount: 1);
                return true;

            case OpcodeMouseWheel:
                if (binary.Length >= 5)
                {
                    var deltaUnits = BitConverter.ToSingle(binary, 1);
                    // Standard wheel delta scaling: 120 units per click
                    var deltaY = (int)Math.Round(deltaUnits * 120.0f);
                    dispatcher?.SendMouseWheel(screenX, screenY, deltaX: 0, deltaY: deltaY);
                    return true;
                }
                break;

            default:
                this.logger.Debug("Unhandled NDI KVM opcode 0x{Opcode:X2}", opcode);
                break;
        }

        return false;
    }

    /// <summary>
    /// Extracts and decodes the Base64 binary payload from an &lt;ndi_kvm&gt; element,
    /// handling both uncompressed ('u') and zlib-compressed ('d') attributes.
    /// </summary>
    public static bool ExtractPayload(string xml, out byte[]? payload)
    {
        payload = null;
        if (string.IsNullOrWhiteSpace(xml))
        {
            return false;
        }

        var trimmed = xml.Trim();

        // Check for uncompressed: <ndi_kvm u="..."
        const string uncompressedPrefix = "<ndi_kvm u=\"";
        const string compressedPrefix = "<ndi_kvm d=\"";

        int prefixIdx = trimmed.IndexOf(uncompressedPrefix, StringComparison.Ordinal);
        bool isCompressed = false;

        if (prefixIdx >= 0)
        {
            prefixIdx += uncompressedPrefix.Length;
        }
        else
        {
            prefixIdx = trimmed.IndexOf(compressedPrefix, StringComparison.Ordinal);
            if (prefixIdx >= 0)
            {
                prefixIdx += compressedPrefix.Length;
                isCompressed = true;
            }
            else
            {
                return false;
            }
        }

        int quoteIdx = trimmed.IndexOf('"', prefixIdx);
        if (quoteIdx < 0)
        {
            return false;
        }

        var base64 = trimmed[prefixIdx..quoteIdx];
        if (string.IsNullOrWhiteSpace(base64))
        {
            return false;
        }

        try
        {
            var rawBytes = Convert.FromBase64String(base64);

            if (isCompressed)
            {
                payload = DecompressZlib(rawBytes);
            }
            else
            {
                payload = rawBytes;
            }

            return payload != null && payload.Length > 0;
        }
        catch
        {
            payload = null;
            return false;
        }
    }

    private static byte[] DecompressZlib(byte[] compressed)
    {
        // Zlib format consists of 2 header bytes (CMF, FLG), deflate compressed data, and 4 adler32 bytes at the end.
        // In .NET, ZLibStream handles this directly.
        using var sourceStream = new MemoryStream(compressed);
        using var zlibStream = new ZLibStream(sourceStream, CompressionMode.Decompress);
        using var resultStream = new MemoryStream();
        zlibStream.CopyTo(resultStream);
        return resultStream.ToArray();
    }
}
