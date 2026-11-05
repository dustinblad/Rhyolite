using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
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

    private async void OnBrowseProfileImageClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider is { CanOpen: true } storageProvider)
            {
                var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Select User Profile Image",
                    AllowMultiple = false,
                    FileTypeFilter =
                    [
                        new FilePickerFileType("Profile Images (.png, .jpg, .jpeg, .webp)")
                        {
                            Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp"]
                        },
                        FilePickerFileTypes.All
                    ]
                });

                if (files is { Count: > 0 } && files[0].TryGetLocalPath() is { Length: > 0 } localPath)
                {
                    vm.ApplyUploadedProfileImageFile(localPath);
                    return;
                }
            }
        }
        catch
        {
            // Fall back to path input or built-in crest if native dialog is unavailable
        }

        vm.UploadProfileImageFromPath();
    }
}
