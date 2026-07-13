using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;

namespace Voiceover.Client.Services;

// Turns plain decrypted message text into WPF Inlines - **bold**, *italic*,
// `code`, bare http(s) URLs as clickable Hyperlinks, and @word as a
// highlighted mention span. Deliberately not validated against the actual
// server member list (would need the rendering path to carry live member
// context, not just the message's own already-decrypted string) - any
// "@word" gets the mention style, so it can highlight things that aren't
// really a member's name. A single, non-nested regex pass (no **bold with
// *italic* inside** support) - message text, not a real markdown document.
public static class MessageContentRenderer
{
    private static readonly Regex TokenPattern = new(
        @"\*\*(?<bold>[^*]+?)\*\*|\*(?<italic>[^*]+?)\*|`(?<code>[^`]+?)`|(?<url>https?://\S+)|(?<mention>@\w+)",
        RegexOptions.Compiled);

    public static List<Inline> BuildInlines(string content)
    {
        var inlines = new List<Inline>();
        if (string.IsNullOrEmpty(content))
            return inlines;

        var lastEnd = 0;
        foreach (Match match in TokenPattern.Matches(content))
        {
            if (match.Index > lastEnd)
                inlines.Add(new Run(content[lastEnd..match.Index]));

            if (match.Groups["bold"].Success)
                inlines.Add(new Bold(new Run(match.Groups["bold"].Value)));
            else if (match.Groups["italic"].Success)
                inlines.Add(new Italic(new Run(match.Groups["italic"].Value)));
            else if (match.Groups["code"].Success)
                inlines.Add(new Run(match.Groups["code"].Value) { FontFamily = new FontFamily("Consolas"), Background = Brushes.Black });
            else if (match.Groups["url"].Success)
            {
                var url = match.Groups["url"].Value;
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https"))
                {
                    var link = new Hyperlink(new Run(url)) { NavigateUri = uri };
                    link.RequestNavigate += OnHyperlinkRequestNavigate;
                    inlines.Add(link);
                }
                else
                {
                    inlines.Add(new Run(url));
                }
            }
            else if (match.Groups["mention"].Success)
            {
                inlines.Add(new Run(match.Groups["mention"].Value)
                {
                    Foreground = (Brush)Application.Current.Resources["AccentBlurple"],
                    FontWeight = FontWeights.Bold
                });
            }

            lastEnd = match.Index + match.Length;
        }

        if (lastEnd < content.Length)
            inlines.Add(new Run(content[lastEnd..]));

        return inlines;
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
