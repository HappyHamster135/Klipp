using Klipp.Core.Updates;
using Klipp.Desktop.Controls;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

/// <summary>Tests the production card in a disposable native window without updater, settings or user media.</summary>
internal static class UpdateReadyPromptChecks
{
    private static readonly string[] PreviewHighlights =
    [
        "See what changed before restarting Klipp.",
        "Keep working while an update waits in the background.",
        "Retry interrupted downloads without closing the app."
    ];

    internal static async Task RunAsync()
    {
        EnsureTheme();
        var (window, root, prompt, focusTarget) = CreateWindow();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        var later = 0;
        var restarts = 0;
        var releaseNotesOpened = 0;
        prompt.LaterRequested += (_, _) => later++;
        prompt.RestartRequested += (_, _) => restarts++;
        prompt.ReleaseNotesRequested += (_, _) => releaseNotesOpened++;
        var policy = new AppUpdatePromptPolicy();
        const string version = "1.2.3";
        void Refresh(string? readyVersion, bool canShow)
        {
            if (policy.ShouldShow(readyVersion, canShow)) prompt.Show(readyVersion!, highlights: PreviewHighlights);
            else prompt.Hide();
        }
        try
        {
            Require(prompt.Visibility == Visibility.Collapsed, "An update card was visible before an update was ready.");
            window.Activate();
            await Task.Delay(120);
            Require(focusTarget.Focus(FocusState.Keyboard), "The disposable window could not focus its library search field.");
            var focused = FocusManager.GetFocusedElement(root.XamlRoot);

            Refresh(version, canShow: false);
            Require(prompt.Visibility == Visibility.Collapsed, "Busy or background state exposed an update prompt.");
            Refresh(version, canShow: true);
            await Task.Delay(220);
            Require(prompt.Visibility == Visibility.Visible, "A ready update did not appear after returning idle.");
            Require(ReferenceEquals(FocusManager.GetFocusedElement(root.XamlRoot), focused), "Showing an update moved keyboard focus.");
            Require(Descendants<TextBlock>(prompt).Any(text => text.Text == "Klipp 1.2.3 is ready to install."),
                "The card did not display the downloaded version.");
            foreach (var highlight in PreviewHighlights)
                Require(Descendants<TextBlock>(prompt).Any(text => text.Text == highlight),
                    "The card omitted a release highlight.");
            Require(Descendants<TextBlock>(prompt).Any(text => text.Text == "What's new"),
                "The highlights did not have a clear heading.");

            var notesLink = Descendants<HyperlinkButton>(prompt).Single();
            Require(Equals(notesLink.Content, "Full release notes") && notesLink.IsEnabled,
                "The full release notes action was missing or disabled.");
            Invoke(notesLink);
            await Task.Delay(20);
            Require(releaseNotesOpened == 1 && later == 0 && restarts == 0,
                "Opening full release notes invoked a restart or dismissal.");
            Require(prompt.Visibility == Visibility.Visible, "Opening full release notes dismissed the card.");

            var surface = (Border)prompt.Content;
            Require(surface.Opacity > .99, "The update card did not finish its entrance animation.");
            for (var refresh = 0; refresh < 5; refresh++) Refresh(version, canShow: true);
            Require(surface.Opacity > .99, "Repeated ready notifications replayed the entrance animation.");
            Require(ReferenceEquals(FocusManager.GetFocusedElement(root.XamlRoot), focused), "Refreshing an update moved keyboard focus.");
            Require(prompt.ActualWidth <= 360.5 && prompt.ActualWidth > 250, "The card did not respect its standard maximum width.");
            var notesViewport = Descendants<ScrollViewer>(prompt).Single(view => view.Name == "NotesViewport");
            Require(notesViewport.ScrollableHeight <= .5 && prompt.ActualHeight < 400,
                "Short highlights created an unnecessarily tall or scrolling card.");

            prompt.Show(version, highlights: ["A refreshed highlight for this update."]);
            Require(Descendants<TextBlock>(prompt).Any(text => text.Text == "A refreshed highlight for this update.") &&
                !Descendants<TextBlock>(prompt).Any(text => PreviewHighlights.Contains(text.Text)),
                "Updating the same version left stale release highlights behind.");
            Require(surface.Opacity > .99 && ReferenceEquals(FocusManager.GetFocusedElement(root.XamlRoot), focused),
                "Refreshing release highlights replayed the entrance or moved keyboard focus.");
            prompt.Show("1.2.4", highlights: []);
            var emptyNotes = Descendants<TextBlock>(prompt).Single(text => text.Name == "NoHighlights");
            Require(emptyNotes.Visibility == Visibility.Visible && emptyNotes.Text == "See the full release notes for details." &&
                !Descendants<TextBlock>(prompt).Any(text => text.Text == "A refreshed highlight for this update."),
                "A new version without notes inherited older changes or invented a summary.");
            Require(notesLink.Visibility == Visibility.Visible && notesLink.IsEnabled,
                "A release without highlights lost access to its full notes.");
            prompt.Show(version, highlights: PreviewHighlights);

            // Hide must be synchronous, including during the entrance animation.
            Refresh(version, canShow: false);
            Require(prompt.Visibility == Visibility.Collapsed, "Starting work left the update prompt visible.");
            Refresh(version, canShow: true);
            Refresh(version, canShow: false);
            Require(prompt.Visibility == Visibility.Collapsed, "Hiding during an entrance waited for the animation.");
            await Task.Delay(180);
            Require(prompt.Visibility == Visibility.Collapsed, "A completed entrance revived a hidden prompt.");
            Refresh(version, canShow: true);
            await Task.Delay(180);

            var buttons = Descendants<Button>(prompt).ToArray();
            var laterButton = buttons.Single(button => Equals(button.Content, "Later"));
            var restartButton = buttons.Single(button => !ReferenceEquals(button, laterButton));
            const string restartFailure = "Your project could not be saved. Keep working and try again when the project folder is available.";
            prompt.Show(version, restartFailure, PreviewHighlights);
            var warning = Descendants<TextBlock>(prompt).Single(text => text.Text == restartFailure);
            Require(warning.Visibility == Visibility.Visible && warning.FontSize == 12,
                "A failed restart did not add compact feedback to the already visible update card.");
            Require(surface.Opacity > .99, "Failure feedback replayed the update card's entrance animation.");
            Require(laterButton.IsEnabled && restartButton.IsEnabled, "Failure feedback disabled an update action.");
            Require(ReferenceEquals(FocusManager.GetFocusedElement(root.XamlRoot), focused), "Failure feedback moved keyboard focus.");
            prompt.Show(version, highlights: PreviewHighlights);
            Require(warning.Visibility == Visibility.Collapsed && warning.Text.Length == 0,
                "Clearing restart failure feedback left the warning visible.");
            Require(surface.Opacity > .99, "Clearing failure feedback replayed the entrance animation.");
            Invoke(restartButton);
            await Task.Delay(20);
            Require(restarts == 1 && later == 0, "Restart invoked an incorrect or repeated action.");
            Invoke(laterButton);
            await Task.Delay(20);
            Require(later == 1 && restarts == 1, "Later invoked an incorrect or repeated action.");
            policy.Defer(version);
            Refresh(version, canShow: true);
            Require(prompt.Visibility == Visibility.Collapsed, "Later did not dismiss the downloaded version.");
            Refresh(version, canShow: false);
            Refresh(version, canShow: true);
            Require(prompt.Visibility == Visibility.Collapsed, "Returning idle repeated a deferred update prompt.");
            Refresh(null, canShow: true);
            Refresh(version, canShow: true);
            Require(prompt.Visibility == Visibility.Collapsed, "A ready-state refresh forgot the user's Later choice.");
            Refresh("1.2.4", canShow: true);
            await Task.Delay(180);
            Require(prompt.Visibility == Visibility.Visible, "Deferring one version suppressed a newer update.");

            // A narrow app window should wrap the copy/actions rather than
            // forcing a fixed-width card beyond its available library area.
            appWindow.Resize(new Windows.Graphics.SizeInt32(340, 600));
            const string longFailure = "The update could not be installed because your project could not be saved. Keep working, check that the project folder is available, then try restarting again.";
            var longHighlight = new string('a', 140);
            prompt.Show("1.2.4", longFailure, [longHighlight, longHighlight + "b", longHighlight + "c", "Must not appear as a fourth bullet."]);
            await Task.Delay(180);
            Require(prompt.ActualWidth <= root.ActualWidth - 47,
                "The update card overflowed a narrow app window.");
            Require(Descendants<Button>(prompt).All(button => button.ActualWidth > 35 && button.ActualHeight >= 32),
                "Narrow layout clipped an update action.");
            foreach (var text in Descendants<TextBlock>(prompt).Where(text => text.Text.Length > 0))
                Require(text.ActualWidth <= prompt.ActualWidth - 31, "Update text overflowed the card's available width.");
            Require(warning.Visibility == Visibility.Visible && warning.Text == longFailure && warning.ActualHeight > 24,
                "A long restart failure did not wrap in the narrow update card.");
            Require(laterButton.IsEnabled && restartButton.IsEnabled, "Narrow failure feedback disabled an update action.");
            var highlightText = Descendants<TextBlock>(prompt).Where(text => text.Name.StartsWith("Highlight", StringComparison.Ordinal)).ToArray();
            Require(highlightText.Length == 3 && highlightText.All(text => text.Text.Length <= 110 && text.Text.EndsWith("…", StringComparison.Ordinal)) &&
                !Descendants<TextBlock>(prompt).Any(text => text.Text == "Must not appear as a fourth bullet."),
                "The card did not bound long notes to three compact highlights.");
            Require(prompt.ActualHeight <= root.ActualHeight - 47,
                "Long notes and failure feedback overflowed the available window height.");
            foreach (var action in new FrameworkElement[] { laterButton, restartButton, notesLink })
            {
                var bounds = action.TransformToVisual(root).TransformBounds(new Windows.Foundation.Rect(0, 0, action.ActualWidth, action.ActualHeight));
                Require(bounds.Y >= 23 && bounds.Bottom <= root.ActualHeight - 23,
                    "A narrow window pushed an update action outside the visible card.");
            }
            prompt.Hide();
            Require(prompt.Visibility == Visibility.Collapsed, "Explicit hide did not collapse the card immediately.");
            Console.WriteLine("PASS: production update card highlights, full-notes action, empty/version refresh, focus preservation, ready/busy safety, repeated refresh, synchronous hide, failure feedback, Later policy and responsive layout");
        }
        finally { window.Close(); }
    }

    internal static void OpenPreview(HeadlessApplication app)
    {
        EnsureTheme();
        var (window, root, prompt, focusTarget) = CreateWindow();
        window.Title = "Klipp · Update card preview";
        var status = root.Children.OfType<TextBlock>().Single(text => text.Name == "PreviewStatus");
        prompt.RestartRequested += (_, _) => { prompt.Hide(); status.Text = "Restart selected. This preview does not install or restart anything."; };
        prompt.LaterRequested += (_, _) => { prompt.Hide(); status.Text = "Later selected. This preview does not save any settings."; };
        prompt.ReleaseNotesRequested += (_, _) => status.Text = "Full release notes selected. This preview does not open a browser.";
        var show = new Button { Content = "Show update card", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(24, 132, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        show.Click += (_, _) => prompt.Show("1.2.3", highlights: PreviewHighlights);
        root.Children.Insert(2, show);
        window.Closed += (_, _) => app.Exit();
        root.Loaded += (_, _) => { focusTarget.Focus(FocusState.Keyboard); prompt.Show("1.2.3", highlights: PreviewHighlights); };
        window.Activate();
        Console.WriteLine("Disposable update card preview is open. It does not check, download, install or save updates. Close the window to exit.");
    }

    private static (Window Window, Grid Root, UpdateReadyPrompt Prompt, TextBox FocusTarget) CreateWindow()
    {
        var root = new Grid
        {
            RequestedTheme = ElementTheme.Dark,
            Background = (Brush)Application.Current.Resources["KlippBackgroundBrush"]
        };
        var heading = new TextBlock
        {
            Text = "Library", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(24, 24, 24, 0), VerticalAlignment = VerticalAlignment.Top
        };
        var focusTarget = new TextBox
        {
            PlaceholderText = "Search clips", Margin = new Thickness(24, 64, 24, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Top
        };
        var status = new TextBlock
        {
            Name = "PreviewStatus", Text = "Disposable preview · no settings, captures or projects are loaded.",
            Margin = new Thickness(24, 184, 24, 0), TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Top, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left,
            Foreground = (Brush)Application.Current.Resources["KlippTextSecondaryBrush"]
        };
        var prompt = new UpdateReadyPrompt
        {
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(24)
        };
        root.Children.Add(heading);
        root.Children.Add(focusTarget);
        root.Children.Add(status);
        root.Children.Add(prompt);
        var window = new Window { Title = "Klipp disposable update card check", Content = root };
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd)).Resize(new Windows.Graphics.SizeInt32(800, 560));
        return (window, root, prompt, focusTarget);
    }

    private static void EnsureTheme()
    {
        var resources = Application.Current.Resources;
        resources.MergedDictionaries.Add(new XamlControlsResources());
        var path = Path.Combine(AppContext.BaseDirectory, "Resources", "Theme.xaml");
        resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Load(File.ReadAllText(path)));
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void Invoke(FrameworkElement button)
    {
        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(button) ?? throw new InvalidOperationException("The update action had no automation peer.");
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
