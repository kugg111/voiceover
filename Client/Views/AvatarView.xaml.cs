using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Voiceover.Client.Services;

namespace Voiceover.Client.Views;

// Reusable avatar/icon renderer: shows the image (clipped to a circle) when
// ImageUrl resolves, otherwise a colored circle with the display name's
// initial - the same fallback pattern used everywhere in this codebase
// before this existed (ServerListItem.Initial etc.), just centralized so
// the ~8 call sites (server rail, message authors, member lists, voice
// member list, DM list, friends list) don't each reimplement it.
public partial class AvatarView : UserControl
{
    public static readonly DependencyProperty ImageUrlProperty =
        DependencyProperty.Register(nameof(ImageUrl), typeof(string), typeof(AvatarView),
            new PropertyMetadata(null, OnAppearanceChanged));

    public static readonly DependencyProperty DisplayNameProperty =
        DependencyProperty.Register(nameof(DisplayName), typeof(string), typeof(AvatarView),
            new PropertyMetadata(string.Empty, OnAppearanceChanged));

    public static readonly DependencyProperty SizeProperty =
        DependencyProperty.Register(nameof(Size), typeof(double), typeof(AvatarView),
            new PropertyMetadata(32.0, OnSizeChanged));

    public string? ImageUrl
    {
        get => (string?)GetValue(ImageUrlProperty);
        set => SetValue(ImageUrlProperty, value);
    }

    public string DisplayName
    {
        get => (string)GetValue(DisplayNameProperty);
        set => SetValue(DisplayNameProperty, value);
    }

    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    // A handful of Discord-ish colors, picked deterministically from the
    // display name's hash so the same person always gets the same fallback
    // color instead of every avatar-less user looking identical.
    private static readonly Brush[] Palette =
    {
        new SolidColorBrush(Color.FromRgb(0x58, 0x65, 0xF2)), // blurple
        new SolidColorBrush(Color.FromRgb(0x3B, 0xA5, 0x5C)), // green
        new SolidColorBrush(Color.FromRgb(0xFA, 0xA6, 0x1A)), // gold
        new SolidColorBrush(Color.FromRgb(0xED, 0x42, 0x45)), // red
        new SolidColorBrush(Color.FromRgb(0xEB, 0x45, 0x9E)), // pink
        new SolidColorBrush(Color.FromRgb(0x5B, 0xC0, 0xDE)), // cyan
    };

    static AvatarView()
    {
        foreach (var brush in Palette) brush.Freeze();
    }

    public AvatarView()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private static void OnAppearanceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((AvatarView)d).Refresh();

    private static void OnSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (AvatarView)d;
        var size = (double)e.NewValue;

        view.RootGrid.Width = size;
        view.RootGrid.Height = size;
        view.FallbackCircle.Width = size;
        view.FallbackCircle.Height = size;
        view.InitialText.FontSize = Math.Max(9, size * 0.45);
        view.AvatarImage.Clip = new EllipseGeometry(new Point(size / 2, size / 2), size / 2, size / 2);
    }

    // string.GetHashCode() is randomized per-process in .NET (a DoS-
    // hardening feature, on by default since .NET Core) - using it here
    // meant the same server/username got a different fallback color on
    // every client and every relaunch, instead of a stable one. This is a
    // plain deterministic hash so "the same name always gets the same
    // color" actually holds across processes.
    private static int StableHash(string s)
    {
        unchecked
        {
            int hash = 23;
            foreach (var c in s) hash = hash * 31 + c;
            return hash;
        }
    }

    private void Refresh()
    {
        var name = string.IsNullOrEmpty(DisplayName) ? "?" : DisplayName;
        InitialText.Text = char.ToUpperInvariant(name[0]).ToString();
        FallbackCircle.Fill = Palette[(uint)StableHash(name) % Palette.Length];

        var url = ImageUrl;
        if (string.IsNullOrEmpty(url))
        {
            AvatarImage.Visibility = Visibility.Collapsed;
            AvatarImage.Source = null;
            return;
        }

        _ = LoadImageAsync(url);
    }

    // Fire-and-forget from Refresh() - every list this renders in
    // (messages, members, friends, DMs, server rail) recreates AvatarView
    // instances on every refresh, so this needs to tolerate ImageUrl having
    // already moved on (a different row reusing this instance, or a fast
    // second Refresh landing before the first finishes) by the time the
    // cache lookup/download completes.
    private async Task LoadImageAsync(string url)
    {
        var image = await AvatarImageCache.GetAsync(url);
        if (url != ImageUrl) return;

        if (image is null)
        {
            // Bad URL, 404, network error - fall back to the initial
            // instead of WPF's broken-image icon.
            AvatarImage.Visibility = Visibility.Collapsed;
            AvatarImage.Source = null;
            return;
        }

        AvatarImage.Source = image;
        AvatarImage.Visibility = Visibility.Visible;
    }
}
