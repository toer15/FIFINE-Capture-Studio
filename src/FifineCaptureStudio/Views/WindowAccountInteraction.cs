using System.Diagnostics;
using FifineCaptureStudio.Core;
using FifineCaptureStudio.Core.Accounts;

namespace FifineCaptureStudio.Views;

/// <summary>
/// The WPF side of account sign-in: Sony's OAuth page opens in the existing WebView2 window, and
/// Microsoft's device-code page opens in the user's default browser. Provider code stays UI-free.
/// </summary>
public sealed class WindowAccountInteraction : IAccountInteraction
{
    public Task<PsnAuthResult?> SignInPlayStationAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(PsnSignInWindow.PromptForAuth());
    }

    public void OpenExternal(Uri uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("Could not open the external sign-in page: " + ex.Message);
        }
    }
}
