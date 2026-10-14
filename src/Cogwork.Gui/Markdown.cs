using System.Diagnostics.CodeAnalysis;
using GObject;
using Gtk;
using Markdig;
using WebKit;

namespace Cogwork.Gui;

[Subclass<Box>]
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
        _webView.SetVexpand(true);
        SetHexpand(true);
        SetVexpand(true);
        Append(_webView);

        var settings = _webView.GetSettings();
        settings.SetEnableJavascript(false);
        WebViewNativeLinkHandler.Apply(_webView);

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
                    --card-bg: #ffffff;
                }}

                /* Match system dark preferences instantly */
                @media (prefers-color-scheme: dark) {{
                    :root {{
                        --text-color: #ffffff;
                        --border-color: #383838;
                        --table-stripe: #2f2f2f;
                        --card-bg: #242424;
                    }}
                }}

                body {{
                    font-family: system-ui, -apple-system, sans-serif;
                    color: var(--text-color);
                    background-color: transparent;
                    line-height: 1.6;
                    padding: 24px;

                    max-width: 1000px;
                    margin: 0 auto;

                    overflow-wrap: break-word;
                    word-wrap: break-word;
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

                /* Inline Code Elements */
                code {{
                    font-family: ui-monospace, monospace;
                    background: var(--table-stripe);
                    padding: 2px 6px;
                    border-radius: 4px;
                    font-size: 0.9em;
                }}

                /* Code Block Container */
                pre {{
                    background: var(--card-bg);
                    border: 1px solid var(--border-color);
                    border-radius: 8px; /* Classic Adwaita card curves */
                    padding: 16px;
                    margin: 20px 0;

                    /* Prevents the block from making the page stretch horizontally */
                    max-width: 100%;
                    overflow-x: auto;
                    box-sizing: border-box;
                }}

                /* Code Elements Inside a Block Container */
                pre code {{
                    display: block;
                    background: transparent;
                    padding: 0;
                    border-radius: 0;
                    font-size: 0.875em;
                    line-height: 1.5;
                    white-space: pre; /* Keeps code spacing intact */
                    word-break: normal;
                    overflow-wrap: normal;
                }}
            </style>
        </head>
        <body>
            {htmlBody}
        </body>
        </html>";
    }
}
