using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AIVTuber.Core.ViewModels;

namespace AIVTuber.App.Views;

public partial class LoginView : UserControl
{
    public LoginView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is AccountViewModel old) old.PropertyChanged -= OnVmChanged;
            if (e.NewValue is AccountViewModel vm) vm.PropertyChanged += OnVmChanged;
            ApplyMode();
        };
    }

    private AccountViewModel? Vm => DataContext as AccountViewModel;

    private async void OnLogin(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        LoginButton.IsEnabled = false;
        try
        {
            await vm.LoginAsync(PasswordInput.Password);
        }
        finally
        {
            // The password never outlives the attempt in the UI.
            PasswordInput.Clear();
            LoginButton.IsEnabled = true;
        }
    }

    private void OnPasswordKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnLogin(sender, e);
    }

    private async void OnRegister(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        RegisterButton.IsEnabled = false;
        try
        {
            await vm.RegisterAsync(InviteBox.Text, RegPasswordInput.Password, RegConfirmInput.Password);
            if (vm.IsSignedIn) InviteBox.Clear();
        }
        finally
        {
            // Passwords never outlive the attempt in the UI.
            RegPasswordInput.Clear();
            RegConfirmInput.Clear();
            RegisterButton.IsEnabled = true;
        }
    }

    private void OnConfirmKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnRegister(sender, e);
    }

    private void OnToggleRegister(object sender, RoutedEventArgs e)
    {
        Vm?.ToggleRegister();
        FocusFirstField();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AccountViewModel.IsRegistering)) ApplyMode();
    }

    private void ApplyMode()
    {
        var registering = Vm?.IsRegistering == true;
        RegisterPanel.Visibility = registering ? Visibility.Visible : Visibility.Collapsed;
        LoginPanel.Visibility = registering ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnDismiss(object sender, RoutedEventArgs e) => Vm?.DismissLogin();

    /// <summary>Puts the caret where the streamer types next: the password when the account
    /// name is already filled in, otherwise the account name.</summary>
    public void FocusFirstField()
    {
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            Control target = Vm?.IsRegistering == true
                ? string.IsNullOrWhiteSpace(InviteBox.Text) ? InviteBox
                    : string.IsNullOrWhiteSpace(RegUsernameBox.Text) ? RegUsernameBox : RegPasswordInput
                : string.IsNullOrWhiteSpace(UsernameBox.Text) ? UsernameBox : PasswordInput;
            target.Focus();
            Keyboard.Focus(target);
        });
    }
}
