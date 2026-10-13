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
                }}

                /* Match system dark preferences instantly */
                @media (prefers-color-scheme: dark) {{
                    :root {{
                        --text-color: #ffffff;
                        --border-color: #383838;
                        --table-stripe: #2f2f2f;
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
                code {{ font-family: monospace; background: var(--table-stripe); padding: 2px 6px; border-radius: 4px; }}
            </style>
        </head>
        <body>
            {htmlBody}
        </body>
        </html>";
    }
}
