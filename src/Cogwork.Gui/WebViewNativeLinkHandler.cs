using System.Runtime.InteropServices;
using Gtk;
using WebKit;

namespace Cogwork.Gui;

/// <summary>
/// Workaround for GirCore bindings for setting decide policy for WebView appearing to be broken.
/// So stuff like:
/// <![CDATA[
/// System.InvalidCastException: Unable to cast object of type 'GObject.Object' to type 'WebKit.PolicyDecision'.
/// at GObject.Value.Extract[T]()
/// at GObject.SignalArgs.Extract[T](Value value)
/// at WebKit.WebView.DecidePolicySignalArgs.get_Decision()
/// at Cogwork.Gui.MarkdownPreviewer.OnWebViewDecidePolicy(WebView sender, DecidePolicySignalArgs args)
/// ]]>
/// </summary>
public static partial class WebViewNativeLinkHandler
{
    private const string WebKitLib = "libwebkitgtk-6.0.so";
    private const string GObjectLib = "libgobject-2.0.so";

    private delegate bool DecidePolicyDelegate(
        IntPtr webViewPtr,
        IntPtr decisionPtr,
        int decisionType,
        IntPtr userData
    );

    // Prevent the Garbage Collector from freeing the callback memory
    private static DecidePolicyDelegate? _cachedDelegate;

    [LibraryImport(GObjectLib, EntryPoint = "g_signal_connect_data")]
    private static partial ulong g_signal_connect_data(
        IntPtr instance,
        [MarshalAs(UnmanagedType.LPStr)] string detailed_signal,
        DecidePolicyDelegate c_handler,
        IntPtr data,
        IntPtr destroy_data,
        int connect_flags
    );

    [LibraryImport(WebKitLib)]
    private static partial IntPtr webkit_navigation_policy_decision_get_navigation_action(
        IntPtr decision
    );

    [LibraryImport(WebKitLib)]
    private static partial int webkit_navigation_action_get_navigation_type(
        IntPtr navigation_action
    );

    [LibraryImport(WebKitLib)]
    private static partial IntPtr webkit_navigation_action_get_request(IntPtr navigation_action);

    [LibraryImport(WebKitLib)]
    private static partial IntPtr webkit_uri_request_get_uri(IntPtr request);

    [LibraryImport(WebKitLib)]
    private static partial void webkit_policy_decision_ignore(IntPtr decision);

    [LibraryImport(WebKitLib)]
    private static partial void webkit_policy_decision_use(IntPtr decision);

    public static void Apply(WebView webView)
    {
        _cachedDelegate = HandleDecidePolicyNative;

        g_signal_connect_data(
            webView.Handle.DangerousGetHandle(),
            "decide-policy",
            _cachedDelegate,
            IntPtr.Zero,
            IntPtr.Zero,
            0
        );
    }

    private static bool HandleDecidePolicyNative(
        IntPtr webViewPtr,
        IntPtr decisionPtr,
        int decisionType,
        IntPtr userData
    )
    {
        // WEBKIT_POLICY_DECISION_TYPE_NAVIGATION_ACTION is 0
        if (decisionType == 0)
        {
            try
            {
                IntPtr navigationAction = webkit_navigation_policy_decision_get_navigation_action(
                    decisionPtr
                );

                // WEBKIT_NAVIGATION_TYPE_LINK_CLICKED is 0
                int navType = webkit_navigation_action_get_navigation_type(navigationAction);

                if (navType == 0)
                {
                    IntPtr request = webkit_navigation_action_get_request(navigationAction);
                    IntPtr uriPtr = webkit_uri_request_get_uri(request);

                    string? uri = Marshal.PtrToStringAnsi(uriPtr);

                    if (!string.IsNullOrEmpty(uri))
                    {
                        LaunchUriExternal(uri);
                    }

                    // Tell WebKit to drop internal redirection rules
                    webkit_policy_decision_ignore(decisionPtr);
                    return true; // Return true to indicate the signal is fully handled
                }
            }
            catch (Exception ex)
            {
                Cog.Error($"Native source-generated P/Invoke exception: {ex.Message}");
            }
        }

        // Let standard internal frames load as planned
        webkit_policy_decision_use(decisionPtr);
        return false;
    }

    private static async void LaunchUriExternal(string uri)
    {
        try
        {
            var uriLauncher = UriLauncher.New(uri);
            await uriLauncher.LaunchAsync(null);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"System launcher failed to resolve URI: {ex.Message}");
        }
    }
}
