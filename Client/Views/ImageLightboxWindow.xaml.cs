using System.Windows;
using System.Windows.Input;
using Voiceover.Client.Services;

namespace Voiceover.Client.Views;

// Discord-style click-to-view image lightbox for inline message attachments
// (see MainWindow.xaml's Image.MouseLeftButtonUp on the attachment preview).
// A dedicated Window rather than another ModalOverlay shape - ModalOverlay is
// built for a fixed-width dialog card, not a full-bleed, resizable, zoomable
// image surface, and this mirrors the existing ScreenShareViewerWindow
// pattern for "view media in its own window" instead.
public partial class ImageLightboxWindow : Wpf.Ui.Controls.FluentWindow
{
    private const double MinScale = 0.1;
    private const double MaxScale = 6.0;
    private double _scale = 1.0;

    public ImageLightboxWindow(string fullImageUrl)
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadImageAsync(fullImageUrl);
        PreviewKeyDown += ImageLightboxWindow_PreviewKeyDown;
    }

    private async Task LoadImageAsync(string fullImageUrl)
    {
        var bitmap = await AttachmentImageCache.GetFullResolutionAsync(fullImageUrl);
        if (bitmap is null)
        {
            LoadingText.Text = "Couldn't load this image.";
            return;
        }

        LoadingText.Visibility = Visibility.Collapsed;
        LightboxImage.Source = bitmap;

        // Fits the image inside the current viewport but never upscales a
        // small image past its real size (matches how most image viewers -
        // and Discord's own lightbox - open by default). The user can still
        // zoom in past 100% with the scroll wheel from here.
        var availableWidth = ImageScrollViewer.ActualWidth > 0 ? ImageScrollViewer.ActualWidth : ActualWidth;
        var availableHeight = ImageScrollViewer.ActualHeight > 0 ? ImageScrollViewer.ActualHeight : ActualHeight;
        var widthRatio = availableWidth / bitmap.PixelWidth;
        var heightRatio = availableHeight / bitmap.PixelHeight;
        _scale = Math.Min(1.0, Math.Min(widthRatio, heightRatio));
        ApplyScale();
    }

    private void ImageScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (LightboxImage.Source is null) return;

        // Scaling the step by the current zoom (not a flat +/-0.1) keeps
        // each wheel notch feeling proportionally similar whether zoomed
        // way out or way in, instead of huge relative jumps near MinScale.
        var step = _scale * 0.15;
        _scale = Math.Clamp(_scale + (e.Delta > 0 ? step : -step), MinScale, MaxScale);
        ApplyScale();
        e.Handled = true;
    }

    private void ApplyScale()
    {
        ZoomTransform.ScaleX = _scale;
        ZoomTransform.ScaleY = _scale;
        ZoomLevelText.Text = $"{Math.Round(_scale * 100)}%";
    }

    private void ImageLightboxWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }
}
