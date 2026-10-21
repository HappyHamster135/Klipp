using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

internal static class CaptureSourceTargets
{
    private static readonly List<Window> Targets = [];

    public static void Open(HeadlessApplication app)
    {
        foreach (var name in new[] { "A", "B" })
        {
            var window = new CaptureTargetWindow(name);
            Targets.Add(window);
            window.Closed += (_, _) =>
            {
                Targets.Remove(window);
                if (Targets.Count == 0) app.Exit();
            };
            window.Activate();
        }
        Console.WriteLine("Capture QA targets are open. They do not access capture devices, settings, or saved projects. Close both windows to exit.");
    }

    private sealed class CaptureTargetWindow : Window
    {
        private readonly DispatcherQueueTimer _timer;
        private readonly Stopwatch _elapsed = Stopwatch.StartNew();
        private readonly Grid _arena;
        private readonly TranslateTransform _motion = new();
        private readonly TextBlock _counter;
        private long _frame;

        public CaptureTargetWindow(string name)
        {
            Title = $"Klipp capture QA · {name}";
            var isA = name == "A";
            var root = new Grid
            {
                Padding = new Thickness(24), RowSpacing = 12,
                Background = Brush(isA ? 24 : 59, isA ? 51 : 34, isA ? 68 : 69),
                RequestedTheme = ElementTheme.Dark
            };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.Children.Add(new TextBlock
            {
                Text = $"Capture test target {name}", FontSize = 24,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Brush(245, 247, 250)
            });
            var explanation = new TextBlock
            {
                Text = "Choose this window in Klipp. Move or resize it while the square and frame counter keep updating. This is disposable test content.",
                FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = Brush(219, 226, 234), MaxLines = 3
            };
            Grid.SetRow(explanation, 1); root.Children.Add(explanation);
            _arena = new Grid { Clip = new RectangleGeometry() };
            var square = new Border
            {
                Width = 96, Height = 64, CornerRadius = new CornerRadius(6),
                Background = isA ? Brush(111, 225, 207) : Brush(247, 197, 112),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                RenderTransform = _motion,
                Child = new TextBlock
                {
                    Text = name, FontSize = 28, FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Foreground = Brush(23, 25, 28), HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            _arena.Children.Add(square);
            _arena.SizeChanged += (_, _) =>
                ((RectangleGeometry)_arena.Clip).Rect = new Windows.Foundation.Rect(0, 0, _arena.ActualWidth, _arena.ActualHeight);
            Grid.SetRow(_arena, 2); root.Children.Add(_arena);
            _counter = new TextBlock
            {
                FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 13,
                Foreground = Brush(245, 247, 250), Text = "Frame 000000 · elapsed 00:00.000"
            };
            Grid.SetRow(_counter, 3); root.Children.Add(_counter);
            Content = root;
            _timer = DispatcherQueue.CreateTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(33);
            _timer.IsRepeating = true;
            _timer.Tick += OnTick;
            _timer.Start();
            Closed += (_, _) => { _timer.Stop(); _timer.Tick -= OnTick; };

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
            var scale = Math.Max(1, GetDpiForWindow(hwnd) / 96d);
            appWindow.Resize(new Windows.Graphics.SizeInt32((int)(640 * scale), (int)(400 * scale)));
            var work = DisplayArea.Primary.WorkArea;
            var x = isA ? work.X + 40 : work.X + Math.Max(80, work.Width - (int)(680 * scale));
            appWindow.Move(new Windows.Graphics.PointInt32(x, work.Y + (isA ? 60 : 100)));
        }

        private void OnTick(DispatcherQueueTimer sender, object args)
        {
            var seconds = _elapsed.Elapsed.TotalSeconds;
            var phase = seconds % 4 / 4;
            var sweep = phase < .5 ? phase * 2 : (1 - phase) * 2;
            _motion.X = sweep * Math.Max(0, _arena.ActualWidth - 96);
            _motion.Y = (Math.Sin(seconds * 1.7) + 1) / 2 * Math.Max(0, _arena.ActualHeight - 64);
            _counter.Text = $"Frame {++_frame:000000} · elapsed {_elapsed.Elapsed:mm\\:ss\\.fff}";
        }

        private static SolidColorBrush Brush(int red, int green, int blue) =>
            new(Windows.UI.Color.FromArgb(255, (byte)red, (byte)green, (byte)blue));

        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    }
}
