using System.Windows;
using SecurePrintManager.Core;
using SecurePrintManager.Database;

namespace SecurePrintManager.UI;

public partial class LoginWindow : Window
{
    private readonly AuthenticationService _authService;
    private readonly CardReader _cardReader;
    private string _detectedCardCode;
    
    public LoginWindow()
    {
        InitializeComponent();
        
        _authService = new AuthenticationService(new DatabaseContext());
        _cardReader = new CardReader();
        _cardReader.CardRead += OnCardRead;
        _cardReader.Initialize("COM3", 9600); // Configurează portul
    }
    
    private void OnCardRead(string cardCode)
    {
        _detectedCardCode = cardCode;
        CardCodeText.Text = cardCode;
        CardStatusText.Text = "Card detectat!";
        CardStatusText.Foreground = System.Windows.Media.Brushes.Green;
        
        // Auto-login
        var result = _authService.AuthenticateByCard(cardCode);
        if (result.Success)
        {
            MessageBox.Show(
                $"Bine ai venit, {result.User.FullName}!\n\nQuota: {result.User.PagesUsed}/{result.User.MonthlyQuota} pagini",
                "Autentificare reușită",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            
            DialogResult = true;
            Close();
        }
        else
        {
            MessageBox.Show(
                result.Message,
                "Autentificare eșuată",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            
            CardStatusText.Text = "Card nevalid";
            CardStatusText.Foreground = System.Windows.Media.Brushes.Red;
        }
    }
    
    private void LoginByPin_Click(object sender, RoutedEventArgs e)
    {
        var pin = PinPasswordBox.Password;
        
        if (string.IsNullOrWhiteSpace(pin))
        {
            MessageBox.Show(
                "Introduce cod PIN!",
                "Eroare",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }
        
        var result = _authService.AuthenticateByPin(pin);
        
        if (result.Success)
        {
            MessageBox.Show(
                $"Bine ai venit, {result.User.FullName}!\n\nQuota: {result.User.PagesUsed}/{result.User.MonthlyQuota} pagini",
                "Autentificare reușită",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            
            DialogResult = true;
            Close();
        }
        else
        {
            MessageBox.Show(
                result.Message,
                "Autentificare eșuată",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
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