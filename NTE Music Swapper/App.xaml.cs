using System.Windows;

namespace NTE_Music_Swapper
{
    public partial class App : System.Windows.Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Display the loading screen as the initial window
            // then hand over to MainWindow after validation is complete
            var loading = new NtePakTool.LoadingWindow();
            MainWindow = loading;
            loading.Show();

            // Run validation asynchronously → then launch MainWindow
            _ = loading.RunAndHandOffAsync();
        }
    }
}
