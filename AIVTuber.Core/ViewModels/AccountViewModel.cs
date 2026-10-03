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
    private bool _isRegistering;
    private string _username = "";
    private string _errorText = "";
    private string _validUntilText = "";
    private LicenseStopReason _stopReason = LicenseStopReason.SignedOut;

    public AccountViewModel(CloudLicense license, string profileId, string username, Action<Action> dispatch)
    {
        _license = license;
        _dispatch = dispatch;
        ProfileId = profileId;
        _username = username;
        _license.Changed += snapshot => _dispatch(() => Apply(snapshot));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string ProfileId { get; }
    public string Username { get => _username; set => Set(ref _username, value); }
    public string VersionText => $"v{AppVersion.Current}";

    public bool IsSignedIn { get => _isSignedIn; private set => Set(ref _isSignedIn, value); }
    public bool ShowLogin { get => _showLogin; private set => Set(ref _showLogin, value); }
    /// <summary>True while the login page shows the invite-code registration form.</summary>
    public bool IsRegistering { get => _isRegistering; private set => Set(ref _isRegistering, value); }
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

    public void ToggleRegister()
    {
        IsRegistering = !IsRegistering;
        ErrorText = "";
    }

    private static readonly System.Text.RegularExpressions.Regex UsernamePattern = new("^[A-Za-z0-9_-]{3,20}$");
    private const int MinPasswordLength = 8;

    /// <summary>Registers with a one-time invite code (the account name is <see cref="Username"/>).
    /// Obvious mistakes are caught here so they never cost a request.</summary>
    public async Task RegisterAsync(string inviteCode, string password, string confirmPassword)
    {
        if (IsBusy) return;
        var invite = (inviteCode ?? "").Trim();
        var username = (Username ?? "").Trim();
        string? problem =
            invite.Length == 0 ? "请填写邀请码"
            : !UsernamePattern.IsMatch(username) ? "账号名需要 3 到 20 位，只能用字母、数字、下划线和横线"
            : (password ?? "").Length < MinPasswordLength ? "密码至少需要 8 位"
            : password != confirmPassword ? "两次输入的密码不一致"
            : null;
        if (problem is not null)
        {
            ErrorText = problem;
            return;
        }

        IsBusy = true;
        ErrorText = "";
        try
        {
            var outcome = await _license.RegisterAsync(invite, username, password!).ConfigureAwait(true);
            if (outcome.Success) IsRegistering = false;
            else ErrorText = outcome.Message;
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
        ValidUntilText = snapshot.AccountValidUntil is not { } until ? ""
            : CloudLicense.IsNoExpiry(until) ? "长期有效"
            : $"有效至 {until.ToLocalTime():yyyy-MM-dd HH:mm}";
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
