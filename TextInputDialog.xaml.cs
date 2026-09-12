using System.Windows;

namespace SeewoAutoLogin
{
    public partial class TextInputDialog : Window
    {
        public string InputText { get; private set; }

        private bool _isPassword;
        /// <summary>
        /// 是否作为密码输入框使用。setter 会同步切换输入框可见性，
        /// 这样无论用构造函数参数还是对象初始化器赋值都能得到一致行为
        /// （曾因初始化器不触发切换，导致明文回显 + 校验恒失败）。
        /// </summary>
        public bool IsPassword
        {
            get => _isPassword;
            set
            {
                _isPassword = value;
                ApplyPasswordMode();
            }
        }

        public TextInputDialog(string title, string prompt, string defaultValue = "", bool isPassword = false)
        {
            InitializeComponent();
            Title = title;
            PromptText.Text = prompt;
            IsPassword = isPassword;
            if (!isPassword) InputBox.Text = defaultValue;
        }

        /// <summary>按当前 IsPassword 切换明文框/密码框的可见性与焦点</summary>
        private void ApplyPasswordMode()
        {
            if (InputBox == null || PasswordBox == null) return;

            if (_isPassword)
            {
                InputBox.Visibility = Visibility.Collapsed;
                PasswordBox.Visibility = Visibility.Visible;
                if (IsLoaded || PasswordBox.IsVisible) PasswordBox.Focus();
            }
            else
            {
                InputBox.Visibility = Visibility.Visible;
                PasswordBox.Visibility = Visibility.Collapsed;
                if (IsLoaded || InputBox.IsVisible) InputBox.Focus();
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
