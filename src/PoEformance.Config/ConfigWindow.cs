using System.Reflection;
using System.Runtime.Versioning;
using DirectN;
using DirectN.Extensions.Com;
using DirectN.Extensions.Utilities;
using WebView2;
using WebView2.Utilities;

namespace PoEformance.Config;

/// <summary>
/// The configuration window: a native window hosting WebView2, talking to the page over
/// JSON web messages.
/// </summary>
/// <remarks>
/// WHY THIS STACK. The official Microsoft.Web.WebView2 .NET binding is built on classic
/// built-in COM interop, which Native AOT does not support - its AOT story runs through
/// CsWinRT and the Windows App SDK, a dependency tree this project has no other reason to
/// carry. The smourier/WebView2Aot bindings (MIT) are generated as source-generated COM
/// ([GeneratedComClass]), carry no UI framework, and target exactly this shape of app: a
/// raw HWND plus a message loop. The official package is still referenced, but ONLY for its
/// native WebView2Loader.dll asset - its managed assemblies are excluded.
///
/// WHY WEB MESSAGES AND NOT HOST OBJECTS. AddHostObjectToScript marshals through
/// IDispatch/VARIANT; JSON strings over PostWebMessage need none of that, serialise via the
/// source-generated context, and keep the protocol testable without a browser. It is also
/// the same bridge model the AHK tool proved out (ahkCall in, header pushes out), so the
/// page architecture carries over.
///
/// The page itself lives on disk next to the executable (ui/), not embedded - the same
/// decision as the offsets schema: edit, reload, no rebuild.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ConfigWindow : Window
{
    private readonly Func<string, string?> _handleMessage;

    private ComObject<ICoreWebView2Controller>? _controller;
    private IComObject<ICoreWebView2>? _webView;

    // The persistent COM callback. Kept in a field on purpose: the browser holds a native
    // reference, and nothing on the managed side would otherwise keep the wrapper alive -
    // a GC would tear the bridge down mid-session.
    private CoreWebView2WebMessageReceivedEventHandler? _messageHandler;
    private EventRegistrationToken _messageToken;

    /// <summary>The virtual origin the ui/ folder is served under.</summary>
    /// <remarks>
    /// NOT file://, and that is load-bearing: the page uses ES modules, and Chromium
    /// treats file:// as an opaque origin whose module fetches fail CORS - the page
    /// renders its markup and CSS but never executes a line of script, which presents as
    /// a dead window with no data and a button that does nothing (the exact first-run
    /// symptom). Mapping the folder to a private virtual host gives the page a real
    /// origin; the files stay on disk and stay hot-editable.
    /// </remarks>
    private const string VirtualHost = "poeformance.ui";

    /// <summary>
    /// Creates the window and starts the async WebView2 initialisation.
    /// </summary>
    /// <param name="handleMessage">
    /// Called for every JSON message the page posts; a non-null return is posted back.
    /// Runs on the window's UI thread.
    /// </param>
    public ConfigWindow(string title, Func<string, string?> handleMessage)
        : base(title)
    {
        ArgumentNullException.ThrowIfNull(handleMessage);
        _handleMessage = handleMessage;

        // Resolves the WebView2 loader. Not fatal when it cannot: the loader also resolves
        // through the normal DLL search next to the executable, where the publish puts it.
        WebView2Utilities.Initialize(Assembly.GetEntryAssembly()!, throwOnError: false);

        string? runtime = WebView2Utilities.GetAvailableCoreWebView2BrowserVersionString();
        if (runtime is null)
        {
            // No Evergreen runtime installed. The page cannot exist, so say it in the
            // window instead of showing nothing.
            Text = $"{title} - WebView2 runtime not found (install the Evergreen runtime)";
            return;
        }

        // The browser profile goes next to the executable, not into the user profile:
        // the tool is a portable folder, and its state should travel with it.
        string userDataFolder = Path.Combine(AppContext.BaseDirectory, "config", "webview2-profile");

        WebView2.Functions.CreateCoreWebView2EnvironmentWithOptions(
            PWSTR.Null,
            PWSTR.From(userDataFolder),
            null!,
            new CoreWebView2CreateCoreWebView2EnvironmentCompletedHandler((result, environment) =>
            {
                environment.CreateCoreWebView2Controller(
                    Handle,
                    new CoreWebView2CreateCoreWebView2ControllerCompletedHandler((_, controller) =>
                        AttachController(controller)));
            }));
    }

    /// <summary>Pushes a JSON message to the page, if the bridge is up.</summary>
    public void Post(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        _webView?.Object.PostWebMessageAsJson(PWSTR.From(json));
    }

    /// <summary>Wires the created controller: bounds, the message bridge, and the page.</summary>
    private void AttachController(ICoreWebView2Controller controller)
    {
        _controller = new ComObject<ICoreWebView2Controller>(controller);
        controller.put_Bounds(ClientRect).ThrowOnError();
        controller.get_CoreWebView2(out ICoreWebView2 webView).ThrowOnError();
        _webView = new ComObject<ICoreWebView2>(webView);

        // Serve ui/ under the virtual origin (see VirtualHost for why file:// cannot work).
        // DENY is the most restrictive access kind that still serves same-origin module
        // loads; nothing here needs cross-origin anything.
        string uiDirectory = Path.Combine(AppContext.BaseDirectory, "ui");
        using (IComObject<ICoreWebView2_3>? webView3 = _webView.As<ICoreWebView2_3>(throwOnError: false))
        {
            webView3?.Object.SetVirtualHostNameToFolderMapping(
                PWSTR.From(VirtualHost),
                PWSTR.From(uiDirectory),
                COREWEBVIEW2_HOST_RESOURCE_ACCESS_KIND.COREWEBVIEW2_HOST_RESOURCE_ACCESS_KIND_DENY).ThrowOnError();
        }

        _messageHandler = new CoreWebView2WebMessageReceivedEventHandler((_, args) =>
        {
            // WebMessageAsJson works for any posted value; the string variant only for
            // strings. The page posts objects, so this is the general read.
            args.get_WebMessageAsJson(out PWSTR message).ThrowOnError();
            string? reply = _handleMessage(message.ToStringAndDispose() ?? "");
            if (reply is not null)
            {
                Post(reply);
            }
        });
        webView.add_WebMessageReceived(_messageHandler, ref _messageToken).ThrowOnError();

        webView.Navigate(PWSTR.From(PageUrl())).ThrowOnError();
        OnFocusChanged(true);
    }

    /// <summary>
    /// The page to load: ui/index.html next to the executable, served via the virtual
    /// host so its modules run - hot-editable like the schema.
    /// </summary>
    private static string PageUrl()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "ui", "index.html");
        if (!File.Exists(path))
        {
            // A data URL keeps the failure visible INSIDE the window, where the person
            // looking for the page already is.
            return "data:text/html,<body style=\"background:%23111;color:%23ccc;font-family:sans-serif\">"
                + $"<h3>ui/index.html not found</h3><p>expected at {Uri.EscapeDataString(path)}</p></body>";
        }

        return $"https://{VirtualHost}/index.html";
    }

    protected override bool OnFocusChanged(bool setOrKill)
    {
        if (setOrKill && _controller is not null)
        {
            _controller.Object.MoveFocus(COREWEBVIEW2_MOVE_FOCUS_REASON.COREWEBVIEW2_MOVE_FOCUS_REASON_PROGRAMMATIC);
            return true;
        }

        return base.OnFocusChanged(setOrKill);
    }

    protected override bool OnResized(WindowResizedType type, SIZE size)
    {
        _controller?.Object.put_Bounds(ClientRect);
        return base.OnResized(type, size);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_webView is not null && _messageHandler is not null)
            {
                _webView.Object.remove_WebMessageReceived(_messageToken);
            }

            _controller?.Object.Close();
            _webView?.Dispose();
            _controller?.Dispose();
        }

        base.Dispose(disposing);
    }
}
