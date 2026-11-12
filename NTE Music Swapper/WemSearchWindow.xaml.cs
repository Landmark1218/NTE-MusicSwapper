using System.Windows;

namespace NtePakTool
{
    public partial class WemSearchWindow : Window
    {
        public string WemName { get; private set; } = "";

        public WemSearchWindow()
        {
            InitializeComponent();
            TxtWemName.Focus();
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            WemName = TxtWemName.Text.Trim();
            DialogResult = true;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
