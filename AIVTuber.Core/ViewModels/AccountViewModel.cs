using System.ComponentModel;
using System.Runtime.CompilerServices;
using AIVTuber.Core.Auth;

namespace AIVTuber.Core.ViewModels;

/// <summary>
/// Login overlay and account strip for distribution builds (AUTH-02). UI-agnostic; updates are
/// marshalled through the injected dispatch delegate like the other view-models.
/// </summary>
public sealed class AccountViewModel : INotifyPropertyChanged
{
    private readonly CloudLicense _license;
    private readonly Action<Action> _dispatch;
    private bool _isSignedIn;
    private bool _showLogin = true;
    private bool _isBusy;
    private string _errorText = "";
    private string _validUntilText = "";
    private LicenseStopReason _stopReason = LicenseStopReason.SignedOut;

    public AccountViewModel(CloudLicense license, string profileId, string username, Action<Action> dispatch)
    {
        _license = license;
        _dispatch = dispatch;
        ProfileId = profileId;
        Username = username;
        _license.Changed += snapshot => _dispatch(() => Apply(snapshot));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string ProfileId { get; }
    public string Username { get; set; }
    public string VersionText => $"v{AppVersion.Current}";

    public bool IsSignedIn { get => _isSignedIn; private set => Set(ref _isSignedIn, value); }
    public bool ShowLogin { get => _showLogin; private set => Set(ref _showLogin, value); }
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
    public string ErrorText { get => _errorText; private set => Set(ref _errorText, value); }
    public string ValidUntilText { get => _validUntilText; private set => Set(ref _validUntilText, value); }
    /// <summary>Why cloud access is closed (account ended vs. verification lost vs. denied).</summary>
    public LicenseStopReason StopReason { get => _stopReason; private set => Set(ref _stopReason, value); }

    public async Task LoginAsync(string password)
    {
        if (IsBusy) return;
        IsBusy = true;
        ErrorText = "";
        try
        {
            var outcome = await _license.LoginAsync(Username.Trim(), password).ConfigureAwait(true);
            if (!outcome.Success) ErrorText = outcome.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public Task LogoutAsync() => _license.LogoutAsync();

    /// <summary>Hides the overlay while signed out so settings and diagnostics stay usable;
    /// cloud work stays blocked by the runtime gate.</summary>
    public void DismissLogin() => ShowLogin = false;

    public void ReopenLogin()
    {
        if (!IsSignedIn) ShowLogin = true;
    }

    private void Apply(LicenseSnapshot snapshot)
    {
        IsSignedIn = snapshot.State == LicenseState.Active;
        StopReason = snapshot.Reason;
        ValidUntilText = snapshot.AccountValidUntil is { } until
            ? $"有效至 {until.ToLocalTime():yyyy-MM-dd HH:mm}"
            : "";
        if (IsSignedIn)
        {
            ErrorText = "";
            ShowLogin = false;
        }
        else
        {
            ShowLogin = true;
            if (snapshot.Message is not ("未登录" or "已退出登录")) ErrorText = snapshot.Message;
        }
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
