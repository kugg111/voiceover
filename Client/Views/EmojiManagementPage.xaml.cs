using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Voiceover.Client.Models;
using Voiceover.Client.Services;

namespace Voiceover.Client.Views;

public class EmojiListItem
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? ImageUrl { get; set; }
}

public partial class EmojiManagementPage : UserControl
{
    private readonly MainWindow _mainWindow;
    private readonly ApiService _api;
    private readonly int _serverId;
    private readonly ObservableCollection<EmojiListItem> _emojis = new();

    public EmojiManagementPage(MainWindow mainWindow, ApiService api, int serverId)
    {
        InitializeComponent();
        _mainWindow = mainWindow;
        _api = api;
        _serverId = serverId;
        EmojiList.ItemsSource = _emojis;

        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        List<EmojiResponse> emojis;
        try
        {
            emojis = await _api.GetServerEmojisAsync(_serverId);
        }
        catch
        {
            // Network blip, server hiccup, etc. - same graceful in-page
            // fallback as BanListPage.LoadAsync.
            EmptyStateText.Text = "Could not load emoji - try again later.";
            EmptyStateText.Visibility = Visibility.Visible;
            return;
        }

        _emojis.Clear();
        foreach (var em in emojis)
            _emojis.Add(new EmojiListItem { Id = em.Id, Name = em.Name, ImageUrl = App.ResolveUploadUrl(em.ImageUrl) });

        EmptyStateText.Text = "No custom emoji yet.";
        EmptyStateText.Visibility = _emojis.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void UploadEmojiButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Image files (*.png;*.jpg;*.jpeg;*.gif;*.webp)|*.png;*.jpg;*.jpeg;*.gif;*.webp"
        };
        if (dialog.ShowDialog() != true) return;

        var name = await _mainWindow.PromptAsync("Emoji Name", "Name (letters, numbers, underscores only)");
        if (string.IsNullOrWhiteSpace(name)) return;

        UploadEmojiButton.IsEnabled = false;
        StatusText.Text = "Uploading...";

        var (upload, uploadError) = await _api.UploadFileAsync(dialog.FileName);
        if (upload is null)
        {
            StatusText.Text = "";
            UploadEmojiButton.IsEnabled = true;
            await _mainWindow.AlertAsync("Error", uploadError ?? "Upload failed.");
            return;
        }

        var (created, createError) = await _api.CreateServerEmojiAsync(_serverId, name.Trim(), upload.Url);
        StatusText.Text = "";
        UploadEmojiButton.IsEnabled = true;

        if (created is null)
        {
            await _mainWindow.AlertAsync("Error", createError ?? "Could not create emoji.");
            return;
        }

        _emojis.Add(new EmojiListItem { Id = created.Id, Name = created.Name, ImageUrl = App.ResolveUploadUrl(created.ImageUrl) });
        EmptyStateText.Visibility = Visibility.Collapsed;
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int emojiId }) return;

        var success = await _api.DeleteServerEmojiAsync(_serverId, emojiId);
        if (!success)
        {
            await _mainWindow.AlertAsync("Error", "Could not delete this emoji.");
            return;
        }

        var item = _emojis.FirstOrDefault(em => em.Id == emojiId);
        if (item is not null) _emojis.Remove(item);
        EmptyStateText.Visibility = _emojis.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
