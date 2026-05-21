using System;
using System.Windows;
using Microsoft.Win32;


namespace NtePakTool
{
    public partial class SmartInputWindow : Window
    {
        public string ResultPath { get; private set; } = "";

        public SmartInputWindow()
        {
            InitializeComponent();
            TxtInput.Focus(); // 창 켜지자마자 바로 붙여넣기(Ctrl+V) 할 수 있게 포커스!
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            // 💡 OpenFileDialog 앞에 'Microsoft.Win32.' 를 붙여서 정확한 출신을 밝혀줍니다.
            var ofd = new Microsoft.Win32.OpenFileDialog { Filter = "Audio Files|*.wav;*.mp3;*.ogg" };
            if (ofd.ShowDialog() == true)
            {
                TxtInput.Text = ofd.FileName; 
            }
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            ResultPath = TxtInput.Text.Trim();
            DialogResult = true;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}