using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using GObject;
using Markdig;
using WebKit;

namespace Cogwork.Gui;

[Subclass<Gtk.Box>]
partial class MarkdownPreviewer
{
    private WebView _webView;
    private MarkdownPipeline _pipeline;

    [MemberNotNull(nameof(_webView))]
    [MemberNotNull(nameof(_pipeline))]
    partial void Initialize()
    {
        _webView = WebView.NewWithProperties([]);
        _webView.SetHexpand(true);
        Append(_webView);

        var contentManager = _webView.GetUserContentManager();
        contentManager.RegisterScriptMessageHandler("heightNotifier", null);

        contentManager.OnScriptMessageReceived += async (sender, args) =>
        {
            try
            {
                var jsResult = await _webView.EvaluateJavascriptAsync(
                    "document.body.clientHeight;"
                );

                var rawHeightText = jsResult.ToDouble();
                int pixelHeight = (int)Math.Ceiling(rawHeightText);

                if (pixelHeight > 1000)
                    pixelHeight = 1000;

                // Resize the wrapper Gtk.Box layout container smoothly
                SetSizeRequest(-1, pixelHeight);
                Console.WriteLine($"Resized: {pixelHeight}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DEBUG] Asynchronous height lookup failed: {ex}");
            }
        };
        // Listen for when WebKit finishes rendering the page layout structure
        // _webView.OnNotify += async (sender, args) => { };

        _pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

        var transparent = new Gdk.RGBA { Alpha = 0.0f };
        _webView.SetBackgroundColor(transparent);
    }

    /// <summary>
    /// Renders raw Markdown source text straight into the GTK component.
    /// </summary>
    public void Render(string markdownText, string? baseDirectory = null)
    {
        // Parse raw text into structured HTML body chunks
        string rawHtmlBody = Markdown.ToHtml(markdownText, _pipeline);

        // Construct base page injected with explicit Libadwaita styles
        string fullyStyledHtml = WrapInAdwaitaCss(rawHtmlBody);

        // Resolve absolute local images by mapping base directory paths safely
        string baseUri = string.IsNullOrEmpty(baseDirectory)
            ? "about:blank"
            : $"file://{Path.GetFullPath(baseDirectory)}/";

        // Push layout safely to WebKit core renderer
        _webView.LoadHtml(fullyStyledHtml, baseUri);
    }

    static string WrapInAdwaitaCss(string htmlBody)
    {
        return $@"
        <!DOCTYPE html>
        <html>
        <head>
            <meta charset='utf-8'>
            <style>
                /* Adwaita Design Tokens for Web Frameworks */
                :root {{
                    --text-color: #242424;
                    --border-color: #e0e0e0;
                    --table-stripe: #f6f6f6;
                    --accent-color: #3584e4;
                }}

                /* Match system dark preferences instantly */
                @media (prefers-color-scheme: dark) {{
                    :root {{
                        --text-color: #ffffff;
                        --border-color: #383838;
                        --table-stripe: #2f2f2f;
                    }}
                }}

                /* CRITICAL: Force the browser wrapper to drop its own scrollbars */
                /* The size code is broken, must use scrollbar
                html, body {{
                    margin: 0;
                    padding: 0;
                    overflow: hidden;
                    height: auto;
                }} */

                body {{
                    font-family: 'Adwaita Sans', system-ui, -apple-system, sans-serif;
                    color: var(--text-color);
                    background-color: transparent;
                    line-height: 1.6;
                    padding: 24px;
                }}

                /* Markdown Tables Setup */
                table {{
                    border-collapse: collapse;
                    width: 100%;
                    margin: 20px 0;
                    font-size: 0.95em;
                }}
                th, td {{
                    border: 1px solid var(--border-color);
                    padding: 10px 14px;
                    text-align: left;
                }}
                th {{
                    background-color: var(--table-stripe);
                    font-weight: bold;
                }}
                tr:nth-child(even) {{
                    background-color: var(--table-stripe);
                }}

                /* Markdown Images Setup */
                img {{
                    max-width: 100%;
                    height: auto;
                    border-radius: 8px; /* Curve styles identical to Adwaita widgets */
                    margin: 16px 0;
                    display: block;
                }}

                a {{ color: var(--accent-color); text-decoration: none; }}
                a:hover {{ text-decoration: underline; }}
                code {{ font-family: monospace; background: var(--table-stripe); padding: 2px 6px; border-radius: 4px; }}
            </style>
            <script>
                function triggerCsharpResize() {{
                    window.webkit.messageHandlers.heightNotifier.postMessage(null);
                }}

                // 1. Fire when the user drags/resizes the app window
                window.addEventListener('resize', triggerCsharpResize);

                // 2. Also fire once on load so the initial content displays at full height
                window.addEventListener('load', triggerCsharpResize);
            </script>
        </head>
        <body>
            {htmlBody}
        </body>
        </html>";
    }
}
