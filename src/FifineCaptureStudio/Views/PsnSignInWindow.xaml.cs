using System.Windows;
using System.Windows.Input;
using FifineCaptureStudio.Core.Streaming.Nixani;
using Microsoft.Web.WebView2.Core;

namespace FifineCaptureStudio.Views;

/// <summary>
/// Hosts Sony's PSN sign-in page and captures the OAuth redirect. The result is the base64 PSN
/// account ID the PS5 expects during link-code registration; no password or token is stored.
/// </summary>
public partial class PsnSignInWindow : Window
{
    private readonly CancellationTokenSource _lifetime = new();
    private bool _started;

    public PsnSignInWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += (_, _) => _lifetime.Cancel();
    }

    public string? AccountId { get; private set; }

    /// <summary>Shows the sign-in window modally and returns the account ID, or null if cancelled.</summary>
    public static string? PromptForAccountId()
    {
        var window = new PsnSignInWindow();
        if (Application.Current?.MainWindow is { IsLoaded: true } owner && !ReferenceEquals(owner, window))
        {
            window.Owner = owner;
        }

        return window.ShowDialog() == true ? window.AccountId : null;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        try
        {
            await Browser.EnsureCoreWebView2Async();
            Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
            Browser.CoreWebView2.Settings.IsZoomControlEnabled = false;
            Browser.CoreWebView2.NavigationStarting += OnNavigationStarting;
            Browser.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
            Browser.CoreWebView2.Navigate(PsnAccountLookup.LoginUrl);
        }
        catch (Exception ex)
        {
            StatusText.Text = "The PlayStation sign-in page could not open: " + ex.Message;
        }
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        // Sony may open account/legal pages in a popup; keep everything in this one window.
        if (!string.IsNullOrWhiteSpace(e.Uri))
        {
            e.Handled = true;
            Browser.CoreWebView2.Navigate(e.Uri);
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!e.Uri.StartsWith(PsnAccountLookup.RedirectPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        e.Cancel = true;
        var code = GetQueryValue(e.Uri, "code");
        if (string.IsNullOrWhiteSpace(code))
        {
            var error = GetQueryValue(e.Uri, "error_description") ?? GetQueryValue(e.Uri, "error");
            StatusText.Text = string.IsNullOrWhiteSpace(error)
                ? "PlayStation Network did not return a sign-in code. Try again."
                : "PlayStation Network sign-in failed: " + error;
            return;
        }

        _ = CompleteSignInAsync(code);
    }

    private async Task CompleteSignInAsync(string code)
    {
        try
        {
            StatusText.Text = "Reading your PlayStation Network account…";
            var accountId = await PsnAccountLookup.ExchangeCodeAsync(code, _lifetime.Token);
            if (string.IsNullOrWhiteSpace(accountId))
            {
                StatusText.Text = "PlayStation Network did not return an account ID.";
                return;
            }

            AccountId = accountId;
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            // window closed
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private static string? GetQueryValue(string uri, string key)
    {
        try
        {
            var query = new Uri(uri).Query.TrimStart('?');
            foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = pair.IndexOf('=');
                if (separator < 0 ||
                    !string.Equals(pair[..separator], key, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return Uri.UnescapeDataString(pair[(separator + 1)..]);
            }
        }
        catch
        {
            // malformed redirect
        }

        return null;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _lifetime.Cancel();
        DialogResult = false;
    }
}
