using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;

namespace Voiceover.Client.Services;

// Turns plain decrypted message text into WPF Inlines - **bold**, *italic*,
// `code`, [text](url) links, bare http(s) URLs as clickable Hyperlinks,
// @word as a highlighted mention span, and "> " lines as blockquotes.
// Deliberately not validated against the actual server member list (would
// need the rendering path to carry live member context, not just the
// message's own already-decrypted string) - any "@word" gets the mention
// style, so it can highlight things that aren't really a member's name. A
// single, non-nested regex pass per line (no **bold with *italic* inside**
// support) - message text, not a real markdown document.
public static class MessageContentRenderer
{
    private static readonly Regex TokenPattern = new(
        @"\*\*(?<bold>[^*]+?)\*\*|\*(?<italic>[^*]+?)\*|`(?<code>[^`]+?)`|\[(?<linktext>[^\]\r\n]+)\]\((?<linkurl>https?://[^\s)]+)\)|(?<url>https?://\S+)|(?<mention>@\w+)",
        RegexOptions.Compiled);

    public static List<Inline> BuildInlines(string content)
    {
        var inlines = new List<Inline>();
        if (string.IsNullOrEmpty(content))
            return inlines;

        var lines = content.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith("> ") || line == ">")
            {
                inlines.Add(BuildBlockquoteLine(line.Length > 1 ? line[2..] : string.Empty));
            }
            else
            {
                inlines.AddRange(TokenizeLine(line));
            }

            if (i < lines.Length - 1)
                inlines.Add(new Run("\n"));
        }

        return inlines;
    }

    // A blockquote line's own tokens (bold/links/etc.) still get parsed -
    // they're just wrapped in a Span so the whole line shares one muted,
    // italicized style with a leading bar, standing in for the left-border
    // treatment a real blockquote would get in an actual document layout
    // (this renders into a single flat TextBlock, not a FlowDocument, so
    // there's no per-line border to hang off of).
    private static Span BuildBlockquoteLine(string text)
    {
        var span = new Span
        {
            Foreground = Application.Current?.Resources["TextMuted"] as Brush ?? Brushes.Gray,
            FontStyle = FontStyles.Italic
        };
        span.Inlines.Add(new Run("▌ "));
        foreach (var inline in TokenizeLine(text))
            span.Inlines.Add(inline);
        return span;
    }

    private static List<Inline> TokenizeLine(string content)
    {
        var result = new List<Inline>();
        var lastEnd = 0;
        foreach (Match match in TokenPattern.Matches(content))
        {
            if (match.Index > lastEnd)
                result.Add(new Run(content[lastEnd..match.Index]));

            if (match.Groups["bold"].Success)
                result.Add(new Bold(new Run(match.Groups["bold"].Value)));
            else if (match.Groups["italic"].Success)
                result.Add(new Italic(new Run(match.Groups["italic"].Value)));
            else if (match.Groups["code"].Success)
                result.Add(new Run(match.Groups["code"].Value) { FontFamily = new FontFamily("Consolas"), Background = Brushes.Black });
            else if (match.Groups["linktext"].Success)
                result.Add(BuildHyperlinkOrPlainText(match.Groups["linktext"].Value, match.Groups["linkurl"].Value));
            else if (match.Groups["url"].Success)
                result.Add(BuildHyperlinkOrPlainText(match.Groups["url"].Value, match.Groups["url"].Value));
            else if (match.Groups["mention"].Success)
            {
                result.Add(new Run(match.Groups["mention"].Value)
                {
                    Foreground = (Brush)Application.Current.Resources["AccentBlurple"],
                    FontWeight = FontWeights.Bold
                });
            }

            lastEnd = match.Index + match.Length;
        }

        if (lastEnd < content.Length)
            result.Add(new Run(content[lastEnd..]));

        return result;
    }

    private static Inline BuildHyperlinkOrPlainText(string displayText, string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https"))
        {
            var link = new Hyperlink(new Run(displayText)) { NavigateUri = uri };
            link.RequestNavigate += OnHyperlinkRequestNavigate;
            return link;
        }

        return new Run(displayText);
    }

    // Hyperlink doesn't open anything on its own (WPF leaves navigation
    // entirely to the app) - shell out to the OS default browser the same
    // way AttachmentLink_MouseLeftButtonUp already does for attachment URLs.
    private static void OnHyperlinkRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // Best-effort - a link that fails to open isn't worth an error dialog.
        }
        e.Handled = true;
    }
}
