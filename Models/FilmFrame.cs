using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using OpenCvSharp;

namespace IrisPxS.Models
{
    public class FilmFrame : INotifyPropertyChanged
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        private int _frameNumber = 1;
        public int FrameNumber
        {
            get => _frameNumber;
            set { _frameNumber = value; OnPropertyChanged(); }
        }

        public string StripId { get; set; } = string.Empty;

        // 原稿画像上の切り出し領域 (正規化座標 0.0 ~ 1.0 または ピクセル座標)
        private OpenCvSharp.Rect _cropRect;
        public OpenCvSharp.Rect CropRect
        {
            get => _cropRect;
            set { _cropRect = value; OnPropertyChanged(); }
        }

        private double _rotationDegrees = 0.0;
        public double RotationDegrees
        {
            get => _rotationDegrees;
            set { _rotationDegrees = value; OnPropertyChanged(); }
        }

        private bool _isSelected = false;
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        // --- フィルムベース色 (未露光オレンジマスク色) ---
        private byte _baseColorR = 215;
        public byte BaseColorR
        {
            get => _baseColorR;
            set { _baseColorR = value; OnPropertyChanged(); OnPropertyChanged(nameof(BaseColorHex)); }
        }

        private byte _baseColorG = 125;
        public byte BaseColorG
        {
            get => _baseColorG;
            set { _baseColorG = value; OnPropertyChanged(); OnPropertyChanged(nameof(BaseColorHex)); }
        }

        private byte _baseColorB = 75;
        public byte BaseColorB
        {
            get => _baseColorB;
            set { _baseColorB = value; OnPropertyChanged(); OnPropertyChanged(nameof(BaseColorHex)); }
        }

        public string BaseColorHex => $"#{BaseColorR:X2}{BaseColorG:X2}{BaseColorB:X2}";

        // --- フィルムプロファイル & 調整パラメータ ---
        private string _profileId = "portra400";
        public string ProfileId
        {
            get => _profileId;
            set { _profileId = value; OnPropertyChanged(); }
        }

        private double _exposure = 0.0; // -2.0 ~ +2.0 EV
        public double Exposure
        {
            get => _exposure;
            set { _exposure = value; OnPropertyChanged(); }
        }

        private double _contrast = 1.0; // 0.5 ~ 2.0
        public double Contrast
        {
            get => _contrast;
            set { _contrast = value; OnPropertyChanged(); }
        }

        private double _saturation = 1.0; // 0.0 ~ 2.0
        public double Saturation
        {
            get => _saturation;
            set { _saturation = value; OnPropertyChanged(); }
        }

        private double _colorTemp = 0.0; // -50 ~ +50 (暖色 / 寒色)
        public double ColorTemp
        {
            get => _colorTemp;
            set { _colorTemp = value; OnPropertyChanged(); }
        }

        private double _tint = 0.0; // -50 ~ +50 (マゼンタ / グリーン)
        public double Tint
        {
            get => _tint;
            set { _tint = value; OnPropertyChanged(); }
        }

        // --- 赤外線 / AI ゴミ・キズ除去設定 ---
        private bool _dustRemovalEnabled = true;
        public bool DustRemovalEnabled
        {
            get => _dustRemovalEnabled;
            set { _dustRemovalEnabled = value; OnPropertyChanged(); }
        }

        private int _dustRemovalStrength = 2; // 1:弱, 2:標準, 3:強
        public int DustRemovalStrength
        {
            get => _dustRemovalStrength;
            set { _dustRemovalStrength = value; OnPropertyChanged(); }
        }

        // --- Exif メタデータ ---
        private string _cameraMake = string.Empty;
        public string CameraMake
        {
            get => _cameraMake;
            set { _cameraMake = value; OnPropertyChanged(); }
        }

        private string _cameraModel = string.Empty;
        public string CameraModel
        {
            get => _cameraModel;
            set { _cameraModel = value; OnPropertyChanged(); }
        }

        private string _lensModel = string.Empty;
        public string LensModel
        {
            get => _lensModel;
            set { _lensModel = value; OnPropertyChanged(); }
        }

        private double _focalLength = 0.0; // mm
        public double FocalLength
        {
            get => _focalLength;
            set { _focalLength = value; OnPropertyChanged(); }
        }

        private double _fNumber = 0.0; // F値
        public double FNumber
        {
            get => _fNumber;
            set { _fNumber = value; OnPropertyChanged(); }
        }

        private string _shutterSpeed = string.Empty; // 秒数文字列または分数
        public string ShutterSpeed
        {
            get => _shutterSpeed;
            set { _shutterSpeed = value; OnPropertyChanged(); }
        }

        private int _iso = 0;
        public int ISO
        {
            get => _iso;
            set { _iso = value; OnPropertyChanged(); }
        }

        private string _exposureCompensation = string.Empty;
        public string ExposureCompensation
        {
            get => _exposureCompensation;
            set { _exposureCompensation = value; OnPropertyChanged(); }
        }

        private DateTime _dateTaken = DateTime.Now;
        public DateTime DateTaken
        {
            get => _dateTaken;
            set { _dateTaken = value; OnPropertyChanged(); }
        }

        private string _notes = string.Empty;
        public string Notes
        {
            get => _notes;
            set { _notes = value; OnPropertyChanged(); }
        }

        // --- サムネイル・プレビュー用画像 ---
        private BitmapSource? _thumbnail;
        public BitmapSource? Thumbnail
        {
            get => _thumbnail;
            set { _thumbnail = value; OnPropertyChanged(); }
        }

        // 生画像（一時保存先またはMatキャッシュ）
        public string? RawImagePath { get; set; }
        public string? IrImagePath { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public FilmFrame CloneMetadata()
        {
            return new FilmFrame
            {
                CameraMake = this.CameraMake,
                CameraModel = this.CameraModel,
                LensModel = this.LensModel,
                FocalLength = this.FocalLength,
                FNumber = this.FNumber,
                ShutterSpeed = this.ShutterSpeed,
                ISO = this.ISO,
                ExposureCompensation = this.ExposureCompensation,
                DateTaken = this.DateTaken,
                ProfileId = this.ProfileId,
                BaseColorR = this.BaseColorR,
                BaseColorG = this.BaseColorG,
                BaseColorB = this.BaseColorB,
                DustRemovalEnabled = this.DustRemovalEnabled,
                DustRemovalStrength = this.DustRemovalStrength
            };
        }
    }
}
