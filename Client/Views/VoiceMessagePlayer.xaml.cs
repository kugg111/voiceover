using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NAudio.Wave;
using Voiceover.Client.Services;

namespace Voiceover.Client.Views;

// Inline voice-message player for a message row - a real UserControl (not
// an attached behavior like AttachmentImageBehavior) since it needs its own
// per-row playback state (position, playing/paused), not just a one-shot
// image load. Fetches through AttachmentAudioCache (authenticated,
// disk-cached) the same way image attachments do, but caches a local file
// path instead of a decoded bitmap - NAudio's AudioFileReader needs a real
// seekable file.
public partial class VoiceMessagePlayer : UserControl
{
    public static readonly DependencyProperty UrlProperty = DependencyProperty.Register(
        nameof(Url), typeof(string), typeof(VoiceMessagePlayer), new PropertyMetadata(null, OnUrlChanged));

    public string? Url
    {
        get => (string?)GetValue(UrlProperty);
        set => SetValue(UrlProperty, value);
    }

    private WaveOutEvent? _output;
    private AudioFileReader? _reader;
    private string? _localFile;
    private DispatcherTimer? _progressTimer;
    private bool _loadFailed;

    public VoiceMessagePlayer()
    {
        InitializeComponent();
        Unloaded += (_, _) => StopAndCleanup();
    }

    // ListBox virtualization recycles rows as they scroll - a recycled
    // instance getting a new Url must forget whatever it was playing for
    // the previous row.
    private static void OnUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is VoiceMessagePlayer player) player.Reset();
    }

    private void Reset()
    {
        StopAndCleanup();
        _loadFailed = false;
        PlayPauseButton.Content = "▶";
        TimeText.Text = "0:00";
        ProgressBarControl.Value = 0;
    }

    private async void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_output is { PlaybackState: PlaybackState.Playing })
        {
            _output.Pause();
            PlayPauseButton.Content = "▶";
            return;
        }

        if (_output is { PlaybackState: PlaybackState.Paused })
        {
            _output.Play();
            PlayPauseButton.Content = "⏸";
            return;
        }

        if (_loadFailed || Url is null) return;

        var url = Url;
        _localFile ??= await AttachmentAudioCache.GetFileAsync(url);

        // The row may have been recycled onto a different message while the
        // fetch above was in flight - bail rather than start playback for a
        // URL this instance no longer represents.
        if (Url != url) return;

        if (_localFile is null)
        {
            _loadFailed = true;
            return;
        }

        _reader = new AudioFileReader(_localFile);
        _output = new WaveOutEvent();
        _output.Init(_reader);
        _output.PlaybackStopped += (_, _) => Dispatcher.Invoke(OnPlaybackStopped);
        _output.Play();
        PlayPauseButton.Content = "⏸";

        ProgressBarControl.Maximum = Math.Max(_reader.TotalTime.TotalSeconds, 0.01);
        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _progressTimer.Tick += (_, _) => UpdateProgress();
        _progressTimer.Start();
    }

    private void UpdateProgress()
    {
        if (_reader is null) return;
        ProgressBarControl.Value = _reader.CurrentTime.TotalSeconds;
        TimeText.Text = $"{(int)_reader.CurrentTime.TotalMinutes}:{_reader.CurrentTime.Seconds:D2}";
    }

    // Fires when playback reaches the end naturally (Pause doesn't raise
    // this) - rewind so a second click replays from the start.
    private void OnPlaybackStopped()
    {
        if (_reader is not null) _reader.Position = 0;
        PlayPauseButton.Content = "▶";
    }

    private void StopAndCleanup()
    {
        _progressTimer?.Stop();
        _progressTimer = null;
        _output?.Dispose();
        _output = null;
        _reader?.Dispose();
        _reader = null;
        _localFile = null;
    }
}
