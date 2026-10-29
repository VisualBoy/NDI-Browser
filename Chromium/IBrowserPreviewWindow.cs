namespace Tractus.HtmlToNdi.Chromium;

/// <summary>
/// Defines the contract for an interactive desktop preview window.
/// </summary>
public interface IBrowserPreviewWindow : IDisposable
{
    /// <summary>
    /// Updates the preview buffer with raw BGRA pixel data from the renderer.
    /// Thread-safe and callable from CEF rendering threads.
    /// </summary>
    /// <param name="bufferHandle">Pointer to the unmanaged BGRA buffer.</param>
    /// <param name="width">Width of the rendered frame.</param>
    /// <param name="height">Height of the rendered frame.</param>
    void UpdateFrame(nint bufferHandle, int width, int height);

    /// <summary>
    /// Closes the preview window.
    /// </summary>
    void Close();
}
