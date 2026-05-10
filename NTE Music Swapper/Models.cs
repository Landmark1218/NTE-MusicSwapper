using System.ComponentModel;

namespace NtePakTool
{
    //共通のデータモデル
    public class WemItemBase : INotifyPropertyChanged
    {
        public string RelativePath { get; set; } = "";
        public string FullPath { get; set; } = "";
        private string _sourceAudioPath = "No file selected";
        public string SourceAudioPath
        {
            get { return _sourceAudioPath; }
            set { _sourceAudioPath = value; OnPropertyChanged("SourceAudioPath"); }
        }
        private bool _isSelected = false;
        public bool IsSelected
        {
            get { return _isSelected; }
            set { _isSelected = value; OnPropertyChanged("IsSelected"); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class WemMapItem : WemItemBase { }

    public class UiWemItem : WemItemBase
    {
        public string DisplayName { get; set; } = "";
        public string ImagePath { get; set; } = "";
    }

    //UI設定のJSON用
    public class UiConfigJson
    {
        public string WemName { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string ImageName { get; set; } = "";
    }
    //アプリ設定のJSON用
    public class AppSettingsJson
    {
        public bool SaveModEnabled { get; set; } = false;
        public long OriginalPakSize { get; set; } = 0;
        public string GameRootDir { get; set; } = "";
        public string WwiseConsolePath { get; set; } = "";
    }
}
