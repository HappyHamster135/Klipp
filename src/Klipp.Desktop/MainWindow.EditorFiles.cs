using System;
using System.Threading.Tasks;
using Windows.Storage.Pickers;

namespace Klipp.Desktop;

public sealed partial class MainWindow
{
    private async void OnEditorSourceMediaImportRequested(object? sender, EventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary };
            foreach (var extension in new[] { ".mp4", ".mov", ".mkv", ".webm", ".avi", ".m4v", ".mts", ".m2ts" })
                picker.FileTypeFilter.Add(extension);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSingleFileAsync();
            if (file is not null)
                await EditorView.OpenSourceMediaAsync(file.Path);
        }
        catch (Exception exception) { await ShowErrorAsync("Could not open source media", exception.Message); }
    }

    private async void OnEditorProjectFileOpenRequested(object? sender, EventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add(".klippproject");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSingleFileAsync();
            if (file is not null)
                await EditorView.OpenProjectFileAsync(file.Path);
        }
        catch (Exception exception) { await ShowErrorAsync("Could not open project", exception.Message); }
    }

    private async void OnEditorProjectMediaCollectRequested(object? sender, EventArgs e)
    {
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
                await EditorView.CollectProjectMediaAsync(folder.Path);
        }
        catch (Exception exception) { await ShowErrorAsync("Could not collect project media", exception.Message); }
    }
}
