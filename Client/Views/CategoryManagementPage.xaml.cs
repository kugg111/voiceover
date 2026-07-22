using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Voiceover.Client.Models;
using Voiceover.Client.Services;

namespace Voiceover.Client.Views;

public class CategoryListItem
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

// Full CRUD + reorder surface for channel categories - the sidebar's own
// category headers (see MainWindow.xaml's ItemsControl.GroupStyle) are
// display-only and only ever appear for categories that currently have at
// least one channel of that type, so an empty (or single-type-only) category
// would otherwise have no way to be renamed/deleted/reordered. This page
// is that canonical management surface, same role EmojiManagementPage plays
// for custom emoji.
public partial class CategoryManagementPage : UserControl
{
    private readonly MainWindow _mainWindow;
    private readonly ApiService _api;
    private readonly int _serverId;
    private readonly ObservableCollection<CategoryListItem> _categories = new();

    public CategoryManagementPage(MainWindow mainWindow, ApiService api, int serverId)
    {
        InitializeComponent();
        _mainWindow = mainWindow;
        _api = api;
        _serverId = serverId;
        CategoryList.ItemsSource = _categories;

        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        List<CategoryResponse> categories;
        try
        {
            categories = await _api.GetCategoriesAsync(_serverId);
        }
        catch
        {
            EmptyStateText.Text = "Could not load categories - try again later.";
            EmptyStateText.Visibility = Visibility.Visible;
            return;
        }

        _categories.Clear();
        foreach (var c in categories.OrderBy(c => c.Position))
            _categories.Add(new CategoryListItem { Id = c.Id, Name = c.Name });

        EmptyStateText.Text = "No categories yet.";
        EmptyStateText.Visibility = _categories.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void AddCategoryButton_Click(object sender, RoutedEventArgs e)
    {
        var name = await _mainWindow.PromptAsync("Add Category", "Category name:");
        if (string.IsNullOrWhiteSpace(name)) return;

        var created = await _api.CreateCategoryAsync(_serverId, name.Trim());
        if (created is null)
        {
            await _mainWindow.AlertAsync("Error", "Could not create this category (you may lack permission).");
            return;
        }

        _categories.Add(new CategoryListItem { Id = created.Id, Name = created.Name });
        EmptyStateText.Visibility = Visibility.Collapsed;
    }

    private async void RenameButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int categoryId }) return;
        var item = _categories.FirstOrDefault(c => c.Id == categoryId);
        if (item is null) return;

        var name = await _mainWindow.PromptAsync("Rename Category", "New name:", item.Name);
        if (string.IsNullOrWhiteSpace(name) || name == item.Name) return;

        var success = await _api.RenameCategoryAsync(_serverId, categoryId, name.Trim());
        if (!success)
        {
            await _mainWindow.AlertAsync("Error", "Could not rename this category (you may lack permission).");
            return;
        }

        await LoadAsync();
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int categoryId }) return;

        if (!await _mainWindow.ConfirmAsync("Delete Category",
                "Delete this category? Its channels will become uncategorized, not deleted.", "Delete", destructive: true))
            return;

        var success = await _api.DeleteCategoryAsync(_serverId, categoryId);
        if (!success)
        {
            await _mainWindow.AlertAsync("Error", "Could not delete this category (you may lack permission).");
            return;
        }

        var item = _categories.FirstOrDefault(c => c.Id == categoryId);
        if (item is not null) _categories.Remove(item);
        EmptyStateText.Visibility = _categories.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void MoveUpButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int categoryId }) return;
        var index = _categories.ToList().FindIndex(c => c.Id == categoryId);
        if (index <= 0) return;

        await ReorderAsync(index, index - 1);
    }

    private async void MoveDownButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int categoryId }) return;
        var index = _categories.ToList().FindIndex(c => c.Id == categoryId);
        if (index < 0 || index >= _categories.Count - 1) return;

        await ReorderAsync(index, index + 1);
    }

    // Swaps two adjacent entries locally, then pushes the whole new order to
    // the server - same "optimistic reorder, revert via reload on failure"
    // shape as MainWindow.ChannelRow_Drop, just triggered by a button instead
    // of a drag.
    private async Task ReorderAsync(int fromIndex, int toIndex)
    {
        _categories.Move(fromIndex, toIndex);

        var success = await _api.ReorderCategoriesAsync(_serverId, _categories.Select(c => c.Id).ToList());
        if (!success) await LoadAsync();
    }
}
