using System.Windows;

namespace SeewoAutoLogin
{
    public partial class TextInputDialog : Window
    {
        public string InputText { get; private set; }
        public bool IsPassword { get; set; }

        public TextInputDialog(string title, string prompt, string defaultValue = "", bool isPassword = false)
        {
            InitializeComponent();
            Title = title;
            PromptText.Text = prompt;
            IsPassword = isPassword;
            InputBox.Text = defaultValue;
            if (isPassword)
            {
                InputBox.Visibility = Visibility.Collapsed;
                PasswordBox.Visibility = Visibility.Visible;
                PasswordBox.Focus();
            }
            else
            {
                InputBox.Visibility = Visibility.Visible;
                PasswordBox.Visibility = Visibility.Collapsed;
                InputBox.Focus();
            }
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            InputText = IsPassword ? PasswordBox.Password : InputBox.Text;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
