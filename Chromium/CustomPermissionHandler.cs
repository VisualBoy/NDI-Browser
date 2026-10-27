using CefSharp;
using Serilog;

namespace Tractus.HtmlToNdi.Chromium;

/// <summary>
/// Automatically grants media access (microphone, webcam) and permission prompt requests.
/// </summary>
internal class CustomPermissionHandler : IPermissionHandler
{
    private readonly ILogger logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CustomPermissionHandler"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    public CustomPermissionHandler(ILogger logger)
    {
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public bool OnRequestMediaAccessPermission(
        IWebBrowser chromiumWebBrowser,
        IBrowser browser,
        IFrame frame,
        string requestingUrl,
        MediaAccessPermissionType requestedPermissions,
        IMediaAccessCallback callback)
    {
        this.logger.Information(
            "Auto-granting media access permission for {Url}: {Permissions}",
            requestingUrl,
            requestedPermissions);

        callback.Continue(requestedPermissions);
        return true;
    }

    /// <inheritdoc/>
    public bool OnShowPermissionPrompt(
        IWebBrowser chromiumWebBrowser,
        IBrowser browser,
        ulong promptId,
        string requestingUrl,
        PermissionRequestType requestedPermissions,
        IPermissionPromptCallback callback)
    {
        this.logger.Information(
            "Auto-granting permission prompt for {Url}: {Permissions} (PromptId={PromptId})",
            requestingUrl,
            requestedPermissions,
            promptId);

        callback.Continue(PermissionRequestResult.Accept);
        return true;
    }

    /// <inheritdoc/>
    public void OnDismissPermissionPrompt(
        IWebBrowser chromiumWebBrowser,
        IBrowser browser,
        ulong promptId,
        PermissionRequestResult result)
    {
        this.logger.Debug(
            "Permission prompt dismissed for PromptId={PromptId} with result {Result}",
            promptId,
            result);
    }
}
