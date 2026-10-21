using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Klipp.Desktop.Services;

internal static class AccessibleDialogFactory
{
    private const double DialogContentMaxWidth = 620;
    private const double DialogContentMaxHeight = 520;
    private const double DialogSurfaceMaxWidth = 680;

    public static ContentDialog Create(
        XamlRoot xamlRoot,
        string title,
        object content,
        string? primaryButtonText = null,
        string? closeButtonText = null,
        ContentDialogButton defaultButton = ContentDialogButton.None,
        string? automationHelpText = null)
    {
        var preparedContent = PrepareContent(title, content);
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = title,
            Content = preparedContent,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = closeButtonText,
            DefaultButton = defaultButton
        };
        // ContentDialog fills the XamlRoot to position its smoke layer and centered
        // surface. Its MaxWidth would constrain that entire overlay, so bound the
        // template's inner BackgroundElement through its dedicated theme resource.
        dialog.Resources["ContentDialogMaxWidth"] = DialogSurfaceMaxWidth;
        if (xamlRoot.Content is FrameworkElement rootElement)
            dialog.RequestedTheme = rootElement.RequestedTheme;

        AutomationProperties.SetName(dialog, title);
        AutomationProperties.SetHelpText(
            dialog,
            automationHelpText ?? (content as string) ?? title);
        return dialog;
    }

    private static UIElement PrepareContent(string title, object content)
    {
        if (content is FrameworkElement contentElement)
        {
            contentElement.MaxWidth = DialogContentMaxWidth;
            contentElement.MaxHeight = DialogContentMaxHeight;
            return contentElement;
        }

        var message = new TextBlock
        {
            Text = content?.ToString() ?? string.Empty,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = DialogContentMaxWidth,
            MaxHeight = DialogContentMaxHeight
        };
        AutomationProperties.SetName(message, $"{title}, content");
        return message;
    }
}
