using System.Windows;
using System.Windows.Input;
using FifineCaptureStudio.Core.Accounts;
using Microsoft.Web.WebView2.Core;

namespace FifineCaptureStudio.Views;

/// <summary>
/// Hosts Sony's PSN sign-in page and captures the OAuth redirect. The result includes the real
/// Online ID plus short-lived tokens for secure storage; no password is ever read by FIFINE.
/// </summary>
public partial class PsnSignInWindow : Window
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly PsnAccountLookup _lookup = new();
    private bool _started;
    private bool _completed;

    public PsnSignInWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            _lifetime.Cancel();
            _lookup.Dispose();
        };
    }

    public PsnAuthResult? Auth { get; private set; }

    /// <summary>Shows Sony's sign-in page and returns the authenticated account, or null if cancelled.</summary>
    public static PsnAuthResult? PromptForAuth()
    {
        var window = new PsnSignInWindow();
        var owner = Application.Current?.Windows
            .OfType<Window>()
            .LastOrDefault(candidate => candidate.IsLoaded && candidate.IsActive && !ReferenceEquals(candidate, window))
            ?? Application.Current?.MainWindow;
        if (owner is { IsLoaded: true } && !ReferenceEquals(owner, window))
        {
            window.Owner = owner;
        }

        return window.ShowDialog() == true ? window.Auth : null;
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
        if (_completed)
        {
            return;
        }

        var code = GetQueryValue(e.Uri, "code");
        if (string.IsNullOrWhiteSpace(code))
        {
            var error = GetQueryValue(e.Uri, "error_description") ?? GetQueryValue(e.Uri, "error");
            StatusText.Text = string.IsNullOrWhiteSpace(error)
                ? "PlayStation Network did not return a sign-in code. Try again."
                : "PlayStation Network sign-in failed: " + error;
            return;
        }

        _completed = true;
        _ = CompleteSignInAsync(code);
    }

    private async Task CompleteSignInAsync(string code)
    {
        try
        {
            StatusText.Text = "Exchanging Sony's sign-in code…";
            var auth = await _lookup.ExchangeCodeAsync(code, _lifetime.Token);
            StatusText.Text = "Reading your PlayStation Network account…";
            if (string.IsNullOrWhiteSpace(auth.OnlineId))
            {
                StatusText.Text = "Sony signed you in but did not return an Online ID. Try again.";
                _completed = false;
                return;
            }

            Auth = auth;
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            // window closed
        }
        catch (AccountAuthException ex)
        {
            StatusText.Text = string.IsNullOrWhiteSpace(ex.Stage) ? ex.Message : $"{ex.Stage}: {ex.Message}";
            _completed = false;
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            _completed = false;
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
