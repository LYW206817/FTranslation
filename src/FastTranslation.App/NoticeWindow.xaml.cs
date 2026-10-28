using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using FastTranslation.Platform.Windows;

namespace FastTranslation.App;

/// <summary>
/// Replaces MessageBox for everything the user has to see (v38). The system dialog ignores the
/// palette: it painted a white slab in the middle of a dark app, and the two reasons it was used —
/// "block until acknowledged" and "ask a yes/no" — both fit here without that cost.
/// </summary>
public partial class NoticeWindow : Window
{
    private readonly string? _linkUrl;
    private readonly bool _confirm;

    private NoticeWindow(string title, string body, string? linkUrl, string? linkLabel, bool confirm)
    {
        InitializeComponent();
        WindowChrome.Apply(this);
        _linkUrl = linkUrl;
        _confirm = confirm;

        // the window title doubles as the headline, the same way every other window does it
        Title = title;
        Headline.Text = title;
        BodyText.Text = body;

        if (!string.IsNullOrEmpty(linkUrl))
        {
            LinkButton.Content = string.IsNullOrEmpty(linkLabel) ? "打开下载页" : linkLabel;
            LinkRow.Visibility = Visibility.Visible;
        }

        if (confirm)
        {
            // a question needs a way to say no; the primary button carries the affirmative answer
            CancelAction.Visibility = Visibility.Visible;
            PrimaryAction.Content = "继续";
        }
    }

    /// <summary>
    /// Blocks until the user closes the dialog. A linkUrl adds one button that opens it in the
    /// browser; pass null when there is nothing to open.
    /// </summary>
    public static void Show(Window? owner, string title, string body, string? linkUrl = null, string? linkLabel = null)
    {
        var window = new NoticeWindow(title, body, linkUrl, linkLabel, confirm: false);
        window.ShowFor(owner);
    }

    /// <summary>Blocks until answered. True when the user picked the affirmative button.</summary>
    public static bool Confirm(Window? owner, string title, string body)
    {
        var window = new NoticeWindow(title, body, null, null, confirm: true);
        return window.ShowFor(owner) == true;
    }

    private bool? ShowFor(Window? owner)
    {
        // an ownerless CenterOwner window lands at an arbitrary spot; the updater often runs while
        // the app sits in the tray with no window to own the dialog
        if (owner is { IsVisible: true })
        {
            Owner = owner;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        return ShowDialog();
    }

    private void Primary_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Link_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_linkUrl)) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = _linkUrl, UseShellExecute = true });
        }
        catch
        {
            // the URL is on screen in the body text as well; a dead browser is not fatal here
            Clipboard.SetText(_linkUrl);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !_confirm)
        {
            DialogResult = false;
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }
}
