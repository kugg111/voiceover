using System.Windows.Documents;
using Voiceover.Client.Services;

namespace Client.Tests;

// Covers bold/italic/code/url/plain-text tokenizing. Deliberately doesn't
// test the @mention token: that branch reads Application.Current.Resources
// (see MessageContentRenderer.cs) for the highlight color, which is null in
// a test process with no running WPF Application - exercising it here would
// need a fake Application/resource setup rather than testing the actual
// parsing logic, so it's left as a disclosed gap rather than a flaky test.
public class MessageContentRendererTests
{
    private static string PlainText(Inline inline) => new TextRange(inline.ContentStart, inline.ContentEnd).Text;

    [Fact]
    public void BuildInlines_EmptyContent_ReturnsEmptyList()
    {
        Assert.Empty(MessageContentRenderer.BuildInlines(""));
    }

    [Fact]
    public void BuildInlines_PlainText_ReturnsSingleRun()
    {
        var inlines = MessageContentRenderer.BuildInlines("just plain text");

        var single = Assert.Single(inlines);
        Assert.IsType<Run>(single);
        Assert.Equal("just plain text", PlainText(single));
    }

    [Fact]
    public void BuildInlines_Bold_WrapsInBoldInline()
    {
        var inlines = MessageContentRenderer.BuildInlines("**bold**");

        var single = Assert.Single(inlines);
        var bold = Assert.IsType<Bold>(single);
        Assert.Equal("bold", PlainText(bold));
    }

    [Fact]
    public void BuildInlines_Italic_WrapsInItalicInline()
    {
        var inlines = MessageContentRenderer.BuildInlines("*italic*");

        var single = Assert.Single(inlines);
        var italic = Assert.IsType<Italic>(single);
        Assert.Equal("italic", PlainText(italic));
    }

    [Fact]
    public void BuildInlines_Code_RendersAsMonospaceRun()
    {
        var inlines = MessageContentRenderer.BuildInlines("`code`");

        var run = Assert.IsType<Run>(Assert.Single(inlines));
        Assert.Equal("code", run.Text);
        Assert.Equal("Consolas", run.FontFamily.Source);
    }

    [Fact]
    public void BuildInlines_HttpUrl_BecomesClickableHyperlink()
    {
        var inlines = MessageContentRenderer.BuildInlines("https://example.com/page");

        var link = Assert.IsType<Hyperlink>(Assert.Single(inlines));
        Assert.Equal("https://example.com/page", link.NavigateUri!.AbsoluteUri);
    }

    [Fact]
    public void BuildInlines_NonHttpScheme_IsNotTreatedAsALink()
    {
        // The regex only matches http(s):// literally, so this never even
        // reaches the Uri.TryCreate scheme check - included to document
        // that "ftp://..." (or anything else) stays plain text.
        var inlines = MessageContentRenderer.BuildInlines("ftp://example.com/file");

        var single = Assert.Single(inlines);
        Assert.IsType<Run>(single);
        Assert.Equal("ftp://example.com/file", PlainText(single));
    }

    [Fact]
    public void BuildInlines_MixedContent_PreservesOrderAndPlainTextBetweenTokens()
    {
        var inlines = MessageContentRenderer.BuildInlines("hello **world** and `code` too");

        Assert.Equal(5, inlines.Count);
        Assert.Equal("hello ", PlainText(inlines[0]));
        Assert.Equal("world", PlainText(Assert.IsType<Bold>(inlines[1])));
        Assert.Equal(" and ", PlainText(inlines[2]));
        Assert.Equal("code", ((Run)inlines[3]).Text);
        Assert.Equal(" too", PlainText(inlines[4]));
    }
}
