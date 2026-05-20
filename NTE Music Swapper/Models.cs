using System.ComponentModel;

namespace NtePakTool
{
    // Common data model
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

    // For UI settings JSON
    // Note: DurationHex field has been removed.
    //       BNK Duration patching now uses CUE4Parse to parse the .bnk file
    //       and automatically finds all fields whose name contains "Duration".
    public class UiConfigJson
    {
        public string WemName { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string ImageName { get; set; } = "";
        public string JsonName { get; set; } = "";
        // DurationHex は削除。既存の ui_wem_config.json に残っていても無視される。
    }

    // For app settings JSON
    public class AppSettingsJson
    {
        public bool SaveModEnabled { get; set; } = true;
        public long OriginalPakSize { get; set; } = 0;
        public string GameRootDir { get; set; } = "";
        public string WwiseConsolePath { get; set; } = "";
        public bool UseYouTubeLink { get; set; } = false;
        public long ModSize { get; set; } = 0;
        public int ModApplyDelay { get; set; } = 1000;
        // PatchPaks (pakchunk3-Windows_0_P.pak) management
        public long OriginalPatchPakSize { get; set; } = 0;
        public long PatchModSize { get; set; } = 0;
    }
}
