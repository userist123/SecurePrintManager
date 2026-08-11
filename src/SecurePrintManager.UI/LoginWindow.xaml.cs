using System.Windows;
using SecurePrintManager.Core;
using SecurePrintManager.Database;

namespace SecurePrintManager.UI;

public partial class LoginWindow : Window
{
    private readonly AuthenticationService _authService;
    private readonly CardReader _cardReader;
    public User? AuthenticatedUser { get; private set; }

    public LoginWindow()
    {
        InitializeComponent();

        var db = new DatabaseContext();
        _authService = new AuthenticationService(db);
        _cardReader = new CardReader();
        _cardReader.CardRead += OnCardRead;
        _cardReader.Initialize("COM3", 9600);
    }

    private void OnCardRead(string cardCode)
    {
        CardCodeText.Text = cardCode;
        CardStatusText.Text = "Card detectat!";
        CardStatusText.Foreground = System.Windows.Media.Brushes.LimeGreen;

        var result = _authService.AuthenticateByCard(cardCode);
        HandleAuthResult(result);
    }

    private void LoginByPin_Click(object sender, RoutedEventArgs e)
    {
        var pin = PinPasswordBox.Password;
        if (string.IsNullOrWhiteSpace(pin))
        {
            MessageBox.Show("Introdu cod PIN.", "Eroare",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = _authService.AuthenticateByPin(pin);
        HandleAuthResult(result);
    }

    private void HandleAuthResult(AuthResult result)
    {
        if (!result.Success || result.User == null)
        {
            MessageBox.Show(result.Message, "Autentificare eșuată",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        AuthenticatedUser = result.User;
        MessageBox.Show(
            $"Bine ai venit, {result.User.FullName}!\nQuota: {result.User.PagesUsed}/{result.User.MonthlyQuota} pagini",
            "Autentificare reușită",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _cardReader.Close();
        base.OnClosed(e);
    }
}
