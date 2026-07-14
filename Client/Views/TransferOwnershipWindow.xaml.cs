using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Voiceover.Client.Models;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Voiceover.Client.Views;

// Shown between the delete-account confirmation and the actual API call
// when ApiService.GetOwnedServersNeedingTransferAsync returns 1+ servers -
// each of those has 2+ other members, so there's no unambiguous auto-pick
// (unlike 0 other members, where the server is just deleted, or exactly 1,
// which is auto-promoted server-side without ever reaching this dialog).
public partial class TransferOwnershipWindow : FluentWindow
{
    private readonly Dictionary<int, ComboBox> _pickers = new();

    public bool Result { get; private set; }
    public List<OwnershipTransfer> Selections { get; } = new();

    public TransferOwnershipWindow(List<OwnedServerNeedingTransferResponse> servers)
    {
        InitializeComponent();

        foreach (var server in servers)
        {
            var label = new TextBlock
            {
                Text = server.ServerName,
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                Foreground = (Brush)FindResource("TextNormal"),
                Margin = new Thickness(0, 0, 0, 4)
            };

            var combo = new ComboBox
            {
                ItemsSource = server.Candidates,
                DisplayMemberPath = nameof(OwnershipCandidate.Username),
                SelectedIndex = 0,
                Margin = new Thickness(0, 0, 0, 16)
            };
            _pickers[server.ServerId] = combo;

            ServersPanel.Children.Add(label);
            ServersPanel.Children.Add(combo);
        }
    }

    private void ContinueButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var (serverId, combo) in _pickers)
        {
            if (combo.SelectedItem is OwnershipCandidate candidate)
                Selections.Add(new OwnershipTransfer(serverId, candidate.UserId));
        }

        Result = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Result = false;
        Close();
    }
}
