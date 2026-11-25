using System.ComponentModel;

namespace FifineCaptureStudio.Core.Accounts;

/// <summary>
/// One platform's account state. The three sessions are deliberately independent: changing one
/// never touches the other two, and none of them is owned by the console/device connection state.
/// </summary>
public sealed class AccountSession : INotifyPropertyChanged
{
    private readonly SynchronizationContext? _context;
    private AccountStatus _status = AccountStatus.SignedOut;
    private string _statusText = string.Empty;
    private string _displayName = string.Empty;
    private string _accountId = string.Empty;
    private string _userCode = string.Empty;
    private string _verificationUri = string.Empty;
    private bool _needsSetup;
    private string _setupMessage = string.Empty;

    public AccountSession(
        AccountPlatform platform,
        string platformLabel,
        string providerLabel,
        string accountIdLabel,
        SynchronizationContext? context = null)
    {
        Platform = platform;
        PlatformLabel = platformLabel;
        ProviderLabel = providerLabel;
        AccountIdLabel = accountIdLabel;
        _context = context;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public AccountPlatform Platform { get; }
    public string PlatformLabel { get; }
    public string ProviderLabel { get; }
    public string AccountIdLabel { get; }

    public AccountStatus Status => _status;
    public string StatusText => _statusText;
    public string DisplayName => _displayName;
    public string AccountId => _accountId;
    public string UserCode => _userCode;
    public string VerificationUri => _verificationUri;
    public bool NeedsSetup => _needsSetup;
    public string SetupMessage => _setupMessage;

    public bool IsSignedIn => _status == AccountStatus.SignedIn;
    public bool IsBusy => _status == AccountStatus.SigningIn;
    public bool HasIdentity => !string.IsNullOrWhiteSpace(_displayName);

    public string StatusLabel => _status switch
    {
        AccountStatus.Unavailable => "Not available",
        AccountStatus.SigningIn => "Signing in…",
        AccountStatus.SignedIn => "Signed in",
        AccountStatus.Error => "Action needed",
        _ => "Signed out"
    };

    public string StatusColor => _status switch
    {
        AccountStatus.SignedIn => "#FF45D69A",
        AccountStatus.SigningIn => "#FF35C9FF",
        AccountStatus.Error => "#FFFF8FA3",
        AccountStatus.Unavailable => "#FF6F7B90",
        _ => "#FF8E9AB2"
    };

    /// <summary>For example "Xbox account: Major Nelson" or "PlayStation account: OnlineId".</summary>
    public string AccountLine => HasIdentity ? $"{PlatformLabel} account: {_displayName}" : string.Empty;

    public string LastAccountLine => HasIdentity ? $"Last account: {_displayName}" : string.Empty;

    public string AccountIdLine => string.IsNullOrWhiteSpace(_accountId)
        ? string.Empty
        : $"{AccountIdLabel}: {_accountId}";

    public bool ShowSignedIn => _status == AccountStatus.SignedIn;
    public bool ShowSignedOut => _status == AccountStatus.SignedOut && !_needsSetup;
    public bool ShowBusy => _status == AccountStatus.SigningIn;
    public bool ShowError => _status == AccountStatus.Error;
    public bool ShowUnavailable => _status == AccountStatus.Unavailable;
    public bool ShowSetup => _needsSetup && _status != AccountStatus.SignedIn;
    public bool ShowDeviceCode => _status == AccountStatus.SigningIn && !string.IsNullOrWhiteSpace(_userCode);
    public bool ShowSignInButton => !_needsSetup && _status is AccountStatus.SignedOut or AccountStatus.Error;
    public bool ShowSignOutButton => _status is AccountStatus.SignedIn or AccountStatus.Error;
    public bool ShowLastAccount => _status == AccountStatus.Error && HasIdentity;
    public bool ShowManualAccountId => Platform == AccountPlatform.PlayStation && _status != AccountStatus.SignedIn;

    public void SetUnavailable(string status)
    {
        Update(() =>
        {
            _status = AccountStatus.Unavailable;
            _statusText = status;
            _displayName = string.Empty;
            _accountId = string.Empty;
            _userCode = string.Empty;
            _verificationUri = string.Empty;
        });
    }

    public void SetSignedOut(string status)
    {
        Update(() =>
        {
            _status = AccountStatus.SignedOut;
            _statusText = status;
            _displayName = string.Empty;
            _accountId = string.Empty;
            _userCode = string.Empty;
            _verificationUri = string.Empty;
            _needsSetup = false;
        });
    }

    public void SetSigningIn(string status)
    {
        Update(() =>
        {
            _status = AccountStatus.SigningIn;
            _statusText = status;
        });
    }

    public void SetSignedIn(string displayName, string accountId, string status)
    {
        Update(() =>
        {
            _status = AccountStatus.SignedIn;
            _displayName = displayName ?? string.Empty;
            _accountId = accountId ?? string.Empty;
            _statusText = status;
            _userCode = string.Empty;
            _verificationUri = string.Empty;
            _needsSetup = false;
        });
    }

    /// <summary>The saved session expired; the last known name is kept for context, but not as a signed-in state.</summary>
    public void SetExpired(string? lastDisplayName, string status)
    {
        Update(() =>
        {
            _status = AccountStatus.Error;
            _displayName = lastDisplayName ?? string.Empty;
            _accountId = string.Empty;
            _statusText = status;
            _userCode = string.Empty;
            _verificationUri = string.Empty;
        });
    }

    public void SetError(string stage, string message)
    {
        Update(() =>
        {
            _status = AccountStatus.Error;
            _statusText = string.IsNullOrWhiteSpace(stage) ? message : $"{stage}: {message}";
            _displayName = string.Empty;
            _accountId = string.Empty;
            _userCode = string.Empty;
            _verificationUri = string.Empty;
        });
    }

    public void SetDeviceCode(string userCode, string verificationUri)
    {
        Update(() =>
        {
            _userCode = userCode ?? string.Empty;
            _verificationUri = verificationUri ?? string.Empty;
        });
    }

    public void ClearDeviceCode()
    {
        Update(() =>
        {
            _userCode = string.Empty;
            _verificationUri = string.Empty;
        });
    }

    public void SetSetup(bool needed, string message)
    {
        Update(() =>
        {
            _needsSetup = needed && _status != AccountStatus.SignedIn;
            _setupMessage = message;
            if (_needsSetup)
            {
                _status = AccountStatus.SignedOut;
                _displayName = string.Empty;
                _accountId = string.Empty;
                _userCode = string.Empty;
                _verificationUri = string.Empty;
            }
        });
    }

    private void Update(Action action)
    {
        if (_context is null || ReferenceEquals(SynchronizationContext.Current, _context))
        {
            action();
            RaiseAll();
            return;
        }

        _context.Post(_ =>
        {
            action();
            RaiseAll();
        }, null);
    }

    private void RaiseAll() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}
