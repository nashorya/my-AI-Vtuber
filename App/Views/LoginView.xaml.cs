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
}
