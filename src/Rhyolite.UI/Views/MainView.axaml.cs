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
                    vm.OnGraphNodeSelected(node);
                }
            };

            GraphCanvas.NodeDoubleClicked += (_, node) =>
            {
                if (DataContext is MainViewModel vm)
                {
                    vm.OnGraphNodeSelected(node);
                    vm.OpenSelectedGraphNote();
                }
            };
        }

        if (CanvasBoard is not null)
        {
            CanvasBoard.CardClicked += card =>
            {
                if (DataContext is MainViewModel vm)
                {
                    vm.SelectCanvasCard(card);
                }
            };

            CanvasBoard.CanvasMutated += (_, doc) =>
            {
                if (DataContext is MainViewModel vm)
                {
                    vm.SaveCanvasLayout(doc);
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
