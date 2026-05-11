using System.Windows;
using System.Windows.Input;

namespace NtePakTool
{
    public partial class YouTubeInputWindow : Window
    {
        public string YouTubeUrl { get; private set; } = "";

        public YouTubeInputWindow()
        {
            InitializeComponent();
            Loaded += (_, _) => TxtUrl.Focus();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            YouTubeUrl = TxtUrl.Text.Trim();
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void TxtUrl_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Enter) Ok_Click(sender, e);
            else if (e.Key == Key.Escape) Cancel_Click(sender, e);
        }
    }
}
