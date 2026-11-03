using Avalonia.Controls;
using Rhyolite.Core.Models;
using Rhyolite.ViewModels;

namespace Rhyolite.Views;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();

        if (GraphCanvas is not null)
        {
            GraphCanvas.NodeSelected += node =>
            {
                if (DataContext is MainViewModel vm)
                {
                    vm.SelectedGraphNodeInfo = $"Selected: {node.Title} (PageRank: {node.PageRank:0.00} · Folder: {node.Folder})";
                }
            };
        }
    }

    private void OnVaultNoteSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainViewModel vm && sender is ListBox lb && lb.SelectedItem is VaultNote note)
        {
            vm.SelectNote(note);
        }
    }
}
