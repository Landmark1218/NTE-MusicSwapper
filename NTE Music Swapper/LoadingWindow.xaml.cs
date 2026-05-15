using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace NtePakTool
{
    public partial class LoadingWindow : Window
    {
        public LoadingWindow()
        {
            InitializeComponent();
        }

        // Safely update progress on the UI thread
        public void SetProgress(VerifierProgressArgs args)
        {
            Dispatcher.Invoke(() =>
            {
                double pct = Math.Clamp(args.Percent * 100, 0, 100);
                PBar.Value = pct;
                TxtPercent.Text = $"{(int)pct}%";
                TxtMessage.Text = args.Message;
            });
        }

        // Run AssetVerifier, then display MainWindow and close itself once completed
        public async Task RunAndHandOffAsync()
        {
            string unpackHtDir = System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "Unpak", "HT");

            var verifier = new AssetVerifier(unpackHtDir, SetProgress);

            try
            {
                await verifier.RunAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                SetProgress(new VerifierProgressArgs
                {
                    Message = $"Warning: {ex.Message}",
                    Percent = 1.0
                });
                await Task.Delay(1500);
            }

            var mainWindow = new MainWindow();
            System.Windows.Application.Current.MainWindow = mainWindow;
            mainWindow.Show();
            Close();
        }
    }
}
