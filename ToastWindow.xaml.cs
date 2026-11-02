using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Valve;

public partial class ToastWindow : Window
{
    private readonly DispatcherTimer _timer;
    private readonly Action? _onPrimary;
    private readonly Action? _onSecondary;

    public ToastWindow(string title, string message, string primaryLabel = "Dismiss", Action? onPrimary = null,
                       string? secondaryLabel = null, Action? onSecondary = null, bool isWarning = false)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        PrimaryBtn.Content = primaryLabel;
        _onPrimary = onPrimary;
        _onSecondary = onSecondary;

        if (string.IsNullOrEmpty(secondaryLabel))
        {
            SecondaryBtn.Visibility = Visibility.Collapsed;
        }
        else
        {
            SecondaryBtn.Content = secondaryLabel;
        }

        if (isWarning)
        {
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(210, 153, 34)); // Warning amber
        }

        Loaded += (s, e) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - Width - 14;
            Top = area.Bottom - Height - 14;
        };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
        _timer.Tick += (s, e) =>
        {
            _timer.Stop();
            Close();
        };
        _timer.Start();
    }

    public static void ShowToast(string title, string message, string primaryLabel = "Dismiss", Action? onPrimary = null,
                                 string? secondaryLabel = null, Action? onSecondary = null, bool isWarning = false)
    {
        Application.Current?.Dispatcher?.InvokeAsync(() =>
        {
            try
            {
                var toast = new ToastWindow(title, message, primaryLabel, onPrimary, secondaryLabel, onSecondary, isWarning);
                toast.Show();
            }
            catch { }
        });
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        Close();
    }

    private void Primary_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        Close();
        _onPrimary?.Invoke();
    }

    private void Secondary_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        Close();
        _onSecondary?.Invoke();
    }
}
