using System.Windows;

namespace NTE_Music_Swapper
{
    public partial class App : System.Windows.Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // ロード画面を最初のウィンドウとして表示し、
            // 検証完了後に MainWindow へ引き渡す
            var loading = new NtePakTool.LoadingWindow();
            MainWindow = loading;
            loading.Show();

            // 非同期で検証 → MainWindow 起動
            _ = loading.RunAndHandOffAsync();
        }
    }
}
