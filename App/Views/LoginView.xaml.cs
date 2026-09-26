using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AIVTuber.Core.ViewModels;

namespace AIVTuber.App.Views;

public partial class LoginView : UserControl
{
    public LoginView() => InitializeComponent();

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

    private void OnDismiss(object sender, RoutedEventArgs e) => Vm?.DismissLogin();

    /// <summary>Puts the caret where the streamer types next: the password when the account
    /// name is already filled in, otherwise the account name.</summary>
    public void FocusFirstField()
    {
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            if (string.IsNullOrWhiteSpace(UsernameBox.Text)) UsernameBox.Focus();
            else PasswordInput.Focus();
            Keyboard.Focus(string.IsNullOrWhiteSpace(UsernameBox.Text) ? UsernameBox : PasswordInput);
        });
    }
}
