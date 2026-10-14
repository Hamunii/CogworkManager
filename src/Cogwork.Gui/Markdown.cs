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

                    --alert-note-color: #0969da;
                    --alert-note-border: #0969da;
                    --alert-tip-color: #1a7f37;
                    --alert-tip-border: #1a7f37;
                    --alert-important-color: #8250df;
                    --alert-important-border: #8250df;
                    --alert-warning-color: #9a6700;
                    --alert-warning-border: #9a6700;
                    --alert-caution-color: #cf222e;
                    --alert-caution-border: #cf222e;
                }}

                /* Match system dark preferences instantly */
                @media (prefers-color-scheme: dark) {{
                    :root {{
                        --text-color: #ffffff;
                        --border-color: #383838;
                        --table-stripe: #2f2f2f;
                        --card-bg: #242424;

                        --alert-note-color: #4493f8;
                        --alert-note-border: #1f6feb;
                        --alert-tip-color: #3fb950;
                        --alert-tip-border: #238636;
                        --alert-important-color: #a371f7;
                        --alert-important-border: #8957e5;
                        --alert-warning-color: #d29922;
                        --alert-warning-border: #9e6a03;
                        --alert-caution-color: #f85149;
                        --alert-caution-border: #da3633;
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

                /* Generic Blockquote Fallback */
                blockquote {{
                    margin: 16px 0;
                    padding: 0 16px;
                    color: var(--text-color);
                    opacity: 0.85;
                    border-left: 4px solid var(--border-color);
                }}

                /* GitHub Flavored Markdown Alerts Base Layout */
                .markdown-alert {{
                    padding: 0px 16px;
                    border-left: 4px solid transparent;
                    border-radius: 0 6px 6px 0;
                    background: transparent;
                }}

                .markdown-alert-title {{
                    display: flex;
                    align-items: center;
                    gap: 8px;
                    font-weight: 600;
                    margin-bottom: 4px;
                }}

                .markdown-alert-title svg {{
                    fill: currentColor !important;
                    stroke: currentColor !important;

                    width: 16px !important;
                    height: 16px !important;
                    flex-shrink: 0 !important;
                    overflow: visible !important;
                    stroke-width: 0.25px !important;
                }}

                .markdown-alert.markdown-alert-note {{
                    border-left-color: var(--alert-note-border);
                }}
                .markdown-alert.markdown-alert-note .markdown-alert-title {{
                    color: var(--alert-note-color);
                }}

                .markdown-alert.markdown-alert-tip {{
                    border-left-color: var(--alert-tip-border);
                }}
                .markdown-alert.markdown-alert-tip .markdown-alert-title {{
                    color: var(--alert-tip-color);
                }}

                .markdown-alert.markdown-alert-important {{
                    border-left-color: var(--alert-important-border);
                }}
                .markdown-alert.markdown-alert-important .markdown-alert-title {{
                    color: var(--alert-important-color);
                }}

                .markdown-alert.markdown-alert-warning {{
                    border-left-color: var(--alert-warning-border);
                }}
                .markdown-alert.markdown-alert-warning .markdown-alert-title {{
                    color: var(--alert-warning-color);
                }}

                .markdown-alert.markdown-alert-caution {{
                    border-left-color: var(--alert-caution-border);
                }}
                .markdown-alert.markdown-alert-caution .markdown-alert-title {{
                    color: var(--alert-caution-color);
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
                    border-radius: 8px;
                    margin: 0 0;
                    display: inline-block;
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
                    border-radius: 8px;
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
                    white-space: pre;
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
