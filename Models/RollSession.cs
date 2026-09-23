using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace IrisPxS.Models
{
    public class FilmStrip : INotifyPropertyChanged
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public int StripIndex { get; set; } = 1;
        public string Name { get; set; } = string.Empty;

        // スキャン全体のプレビュー画像およびIR画像の保存パス
        public string? FullScanImagePath { get; set; }
        public string? FullScanIrPath { get; set; }

        public int ScanDpi { get; set; } = 2400;
        public DateTime ScannedAt { get; set; } = DateTime.Now;

        private ObservableCollection<FilmFrame> _frames = new();
        public ObservableCollection<FilmFrame> Frames
        {
            get => _frames;
            set { _frames = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class RollSession : INotifyPropertyChanged
    {
        public string SessionId { get; set; } = Guid.NewGuid().ToString("N");

        private string _rollName = $"Roll_{DateTime.Now:yyyyMMdd_HHmm}";
        public string RollName
        {
            get => _rollName;
            set { _rollName = value; OnPropertyChanged(); }
        }

        private string _filmStock = string.Empty;
        public string FilmStock
        {
            get => _filmStock;
            set { _filmStock = value; OnPropertyChanged(); }
        }

        private bool _isColor = true;
        public bool IsColor
        {
            get => _isColor;
            set { _isColor = value; OnPropertyChanged(); }
        }

        private bool _isNegative = true;
        public bool IsNegative
        {
            get => _isNegative;
            set { _isNegative = value; OnPropertyChanged(); }
        }

        private int _defaultIso = 0;
        public int DefaultIso
        {
            get => _defaultIso;
            set { _defaultIso = value; OnPropertyChanged(); }
        }

        private string _defaultCamera = string.Empty;
        public string DefaultCamera
        {
            get => _defaultCamera;
            set { _defaultCamera = value; OnPropertyChanged(); }
        }

        private string _defaultLens = string.Empty;
        public string DefaultLens
        {
            get => _defaultLens;
            set { _defaultLens = value; OnPropertyChanged(); }
        }

        private FilmFormatType _selectedFormatType = FilmFormatType.Format135_Full;
        public FilmFormatType SelectedFormatType
        {
            get => _selectedFormatType;
            set { _selectedFormatType = value; OnPropertyChanged(); }
        }

        private double _defaultCropInsetPercent = 3.0;
        public double DefaultCropInsetPercent
        {
            get => _defaultCropInsetPercent;
            set { _defaultCropInsetPercent = value; OnPropertyChanged(); }
        }

        private ObservableCollection<FilmStrip> _strips = new();
        public ObservableCollection<FilmStrip> Strips
        {
            get => _strips;
            set { _strips = value; OnPropertyChanged(); }
        }

        private ObservableCollection<FilmFrame> _allFrames = new();
        public ObservableCollection<FilmFrame> AllFrames
        {
            get => _allFrames;
            set { _allFrames = value; OnPropertyChanged(); }
        }

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public void ReindexFrames()
        {
            int index = 1;
            foreach (var strip in Strips)
            {
                foreach (var frame in strip.Frames)
                {
                    frame.FrameNumber = index++;
                }
            }
        }
    }
}
