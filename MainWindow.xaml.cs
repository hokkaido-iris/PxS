using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using IrisPxS.Models;
using IrisPxS.Services;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;

namespace IrisPxS
{
    public partial class MainWindow : System.Windows.Window
    {
        private readonly ScannerService _scannerService = new();
        private readonly FilmNegativeEngine _negativeEngine = new();
        private readonly FrameDetectorService _detectorService = new();
        private readonly DustScratchRemovalService _dustService = new();
        private readonly ExifMetadataService _exifService = new();
        private readonly RollSessionService _sessionService = new();
        private readonly RollExportService _exportService;

        private RollSession _currentRoll = new();
        private FilmStrip? _currentStrip = null;
        private FilmFrame? _selectedFrame = null;
        private ScannerDeviceInfo? _activeScanner = null;

        private Mat? _currentScanMat = null;
        private Mat? _currentIrMat = null;
        private bool _isUpdatingUi = false;

        public MainWindow()
        {
            InitializeComponent();

            _exportService = new RollExportService(_negativeEngine, _dustService, _exifService);

            InitializeFormatCombo();
            InitializeProfileCombo();
            InitializeSession();

            MainCanvas.FrameSelected += MainCanvas_FrameSelected;
            MainCanvas.FrameModified += MainCanvas_FrameModified;
            MainCanvas.ColorPicked += MainCanvas_ColorPicked;

            Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await RefreshScannersAsync();
        }

        private void InitializeFormatCombo()
        {
            var formats = FilmFormat.GetPresetFormats();
            ComboFilmFormat.ItemsSource = formats;
            ComboFilmFormat.DisplayMemberPath = nameof(FilmFormat.DisplayName);
            ComboFilmFormat.SelectedIndex = 0; // 35mm Full
        }

        private void InitializeProfileCombo()
        {
            var profiles = FilmProfile.GetPresetProfiles();
            ComboFilmProfiles.ItemsSource = profiles;
            ComboFilmProfiles.DisplayMemberPath = nameof(FilmProfile.Name);
            ComboFilmProfiles.SelectedValuePath = nameof(FilmProfile.Id);
            ComboFilmProfiles.SelectedIndex = 0; // Portra 400
        }

        private void InitializeSession()
        {
            _currentRoll = new RollSession();
            
            // 業務用システム仕様：デフォルト入力を一切入れず、ユーザーが入力する空白状態にする
            TxtRollName.Text = string.Empty;
            TxtRollFilmStock.Text = string.Empty;
            TxtRollCamera.Text = string.Empty;
            TxtRollLens.Text = string.Empty;

            ListStrips.ItemsSource = _currentRoll.Strips;
            ListThumbnails.ItemsSource = _currentRoll.AllFrames;
        }

        private async Task RefreshScannersAsync()
        {
            TxtScannerStatus.Text = "スキャナー検索中...";
            TxtStatusMessage.Text = "スキャナーを検索しています...";

            var scanners = await _scannerService.GetConnectedScannersAsync();
            var gtx = scanners.FirstOrDefault(s => s.IsEpsonGtx820) ?? scanners.FirstOrDefault();

            if (gtx != null)
            {
                _activeScanner = gtx;
                TxtScannerStatus.Text = $"{gtx.Name} (接続中)";
                TxtScannerStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 100, 0));
                TxtStatusDevice.Text = "GT-X820: 準備完了";
                TxtStatusMessage.Text = "スキャナーが検出されました。フィルムをセットして [PreScan] を押してください。";
            }
            else
            {
                _activeScanner = new ScannerDeviceInfo { Name = "EPSON GT-X820 (未検出)", IsConnected = false };
                TxtScannerStatus.Text = "装置未検出 (テストモード)";
                TxtScannerStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(160, 0, 0));
                TxtStatusDevice.Text = "装置未検出";
                TxtStatusMessage.Text = "GT-X820 が見つかりません。USB接続を確認してください。";
            }
        }

        private int GetSelectedScanDpi()
        {
            if (ComboScanDpi.SelectedItem is ComboBoxItem item && item.Content != null)
            {
                string text = item.Content.ToString()!;
                if (text.Contains("12800")) return 12800;
                if (text.Contains("9600")) return 9600;
                if (text.Contains("6400")) return 6400;
                if (text.Contains("4800")) return 4800;
                if (text.Contains("3200")) return 3200;
                if (text.Contains("2400")) return 2400;
                if (text.Contains("1200")) return 1200;
            }
            return 2400;
        }

        private FilmFormat GetSelectedFormat()
        {
            return (ComboFilmFormat.SelectedItem as FilmFormat) ?? FilmFormat.GetPresetFormats()[0];
        }

        // --- スキャン処理 ---

        private async void BtnPreScan_Click(object sender, RoutedEventArgs e)
        {
            await RunScanAsync(300, isPreScan: true);
        }

        private async void BtnScanCurrent_Click(object sender, RoutedEventArgs e)
        {
            int dpi = GetSelectedScanDpi();
            await RunScanAsync(dpi, isPreScan: false);
        }

        private async Task RunScanAsync(int dpi, bool isPreScan)
        {
            BtnPreScan.IsEnabled = false;
            BtnScanCurrent.IsEnabled = false;
            ProgressBarMain.Visibility = Visibility.Visible;
            ProgressBarMain.IsIndeterminate = true;

            var progress = new Progress<string>(msg =>
            {
                TxtStatusMessage.Text = msg;
            });

            try
            {
                bool isTransmissive = ChkTransmissive.IsChecked == true;
                var (colorMat, irMat) = await _scannerService.ScanAsync(_activeScanner, dpi, isTransmissive, null, progress);

                string stripName = isPreScan ? $"PreScan_{DateTime.Now:HHmmss}" : $"Strip {_currentRoll.Strips.Count + 1}";
                ApplyNewScanData(stripName, colorMat, irMat, dpi, ChkAutoDetectAfterScan.IsChecked == true);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"スキャン中にエラーが発生しました: {ex.Message}", "スキャンエラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnPreScan.IsEnabled = true;
                BtnScanCurrent.IsEnabled = true;
                ProgressBarMain.Visibility = Visibility.Collapsed;
            }
        }

        private void ApplyNewScanData(string stripName, Mat colorMat, Mat? irMat, int dpi, bool autoDetect)
        {
            _isUpdatingUi = true;
            try
            {
                // 既存の保持Matを安全に解放
                if (_currentScanMat != null && !_currentScanMat.IsDisposed)
                {
                    _currentScanMat.Dispose();
                }
                if (_currentIrMat != null && !_currentIrMat.IsDisposed)
                {
                    _currentIrMat.Dispose();
                }

                // 新しいMatの参照をクローンして保持
                _currentScanMat = colorMat.Clone();
                _currentIrMat = irMat?.Clone();

                // 新しいストリップを作成
                var strip = new FilmStrip
                {
                    Name = stripName,
                    StripIndex = _currentRoll.Strips.Count + 1,
                    ScanDpi = dpi
                };

                // 一時ストレージに保存
                var (rawPath, irPath) = _sessionService.SaveStripImages(_currentRoll.SessionId, strip.Id, _currentScanMat, _currentIrMat);
                strip.FullScanImagePath = rawPath;
                strip.FullScanIrPath = irPath;

                _currentRoll.Strips.Add(strip);
                _currentStrip = strip;
                ListStrips.SelectedItem = strip;

                // キャンバスに表示
                MainCanvas.ImageSource = _negativeEngine.MatToBitmapSource(_currentScanMat);
                MainCanvas.Frames = strip.Frames;

                if (autoDetect)
                {
                    AutoDetectFramesForCurrentStrip();
                }
                else
                {
                    MainCanvas.ResetView();
                }
            }
            finally
            {
                _isUpdatingUi = false;
            }
        }

        private void AutoDetectFramesForCurrentStrip()
        {
            if (_currentScanMat == null || _currentScanMat.IsDisposed || _currentStrip == null) return;

            TxtStatusMessage.Text = "コマ自動認識を実行中...";
            var format = GetSelectedFormat();
            var detectedRects = _detectorService.DetectFrames(_currentScanMat, format);

            _currentStrip.Frames.Clear();

            // ベースカラー自動検知
            var baseColor = _negativeEngine.DetectBaseColor(_currentScanMat);

            int startNumber = _currentRoll.AllFrames.Count + 1;
            foreach (var rect in detectedRects)
            {
                // 空白Exifで初期化（Leica等の決め打ちは一切入れない）
                var frame = new FilmFrame
                {
                    StripId = _currentStrip.Id,
                    FrameNumber = startNumber++,
                    CropRect = rect,
                    BaseColorR = baseColor.R,
                    BaseColorG = baseColor.G,
                    BaseColorB = baseColor.B,
                    ProfileId = (ComboFilmProfiles.SelectedValue as string) ?? "portra400",
                    CameraMake = string.Empty,
                    CameraModel = TxtRollCamera.Text.Trim(),
                    LensModel = TxtRollLens.Text.Trim(),
                    ISO = _currentRoll.DefaultIso,
                    FNumber = 0.0,
                    ShutterSpeed = string.Empty,
                    FocalLength = 0.0,
                    ExposureCompensation = string.Empty,
                    Notes = string.Empty
                };

                // コマ生画像を切り出してキャッシュ
                int x = Math.Clamp(rect.X, 0, _currentScanMat.Width - 1);
                int y = Math.Clamp(rect.Y, 0, _currentScanMat.Height - 1);
                int w = Math.Clamp(rect.Width, 1, _currentScanMat.Width - x);
                int h = Math.Clamp(rect.Height, 1, _currentScanMat.Height - y);

                using (var roi = _currentScanMat[new OpenCvSharp.Rect(x, y, w, h)])
                {
                    frame.RawImagePath = _sessionService.SaveFrameRawImage(_currentRoll.SessionId, frame.Id, roi);
                }

                if (_currentIrMat != null && !_currentIrMat.IsDisposed && !_currentIrMat.Empty())
                {
                    using (var irRoi = _currentIrMat[new OpenCvSharp.Rect(x, y, w, h)])
                    {
                        frame.IrImagePath = _sessionService.SaveFrameRawImage(_currentRoll.SessionId, $"{frame.Id}_ir", irRoi);
                    }
                }

                UpdateFrameThumbnail(frame);
                _currentStrip.Frames.Add(frame);
            }

            SyncAllFramesList();
            MainCanvas.ResetView();

            if (_currentStrip.Frames.Count > 0)
            {
                SelectFrame(_currentStrip.Frames[0]);
            }

            TxtStatusMessage.Text = $"{detectedRects.Count} 個のコマを検出しました。";
        }

        private void SyncAllFramesList()
        {
            _currentRoll.AllFrames.Clear();
            int idx = 1;
            foreach (var s in _currentRoll.Strips)
            {
                foreach (var f in s.Frames)
                {
                    f.FrameNumber = idx++;
                    _currentRoll.AllFrames.Add(f);
                }
            }
            TxtTotalFramesCount.Text = $"全 {_currentRoll.AllFrames.Count} コマ";
        }

        private void UpdateFrameThumbnail(FilmFrame frame)
        {
            if (_currentScanMat == null || _currentScanMat.IsDisposed || frame.CropRect.Width <= 0 || frame.CropRect.Height <= 0) return;

            try
            {
                var r = frame.CropRect;
                int x = Math.Clamp(r.X, 0, _currentScanMat.Width - 1);
                int y = Math.Clamp(r.Y, 0, _currentScanMat.Height - 1);
                int w = Math.Clamp(r.Width, 1, _currentScanMat.Width - x);
                int h = Math.Clamp(r.Height, 1, _currentScanMat.Height - y);

                using var roi = _currentScanMat[new OpenCvSharp.Rect(x, y, w, h)];
                using var positiveMat = _negativeEngine.ConvertNegativeToPositive(roi, frame);

                using var thumb = new Mat();
                Cv2.Resize(positiveMat, thumb, new OpenCvSharp.Size(120, (int)(120 / 1.5)));

                frame.Thumbnail = _negativeEngine.MatToBitmapSource(thumb);
            }
            catch { }
        }

        private void SelectFrame(FilmFrame frame)
        {
            _selectedFrame = frame;
            MainCanvas.SelectedFrame = frame;
            ListThumbnails.SelectedItem = frame;

            UpdateRightPanelFromFrame(frame);
            UpdatePositivePreview();
        }

        private void UpdateRightPanelFromFrame(FilmFrame frame)
        {
            _isUpdatingUi = true;
            try
            {
                TxtSelectedFrameNo.Text = $"コマ #{frame.FrameNumber:D2}";

                // ベースカラー
                BorderBaseColorPreview.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(frame.BaseColorR, frame.BaseColorG, frame.BaseColorB));
                TxtBaseColorHex.Text = $"{frame.BaseColorHex}";

                // プロファイル
                ComboFilmProfiles.SelectedValue = frame.ProfileId;

                // スライダー
                SliderExposure.Value = frame.Exposure;
                TxtExposureVal.Text = frame.Exposure.ToString("0.0");

                SliderContrast.Value = frame.Contrast;
                TxtContrastVal.Text = frame.Contrast.ToString("0.0");

                SliderSaturation.Value = frame.Saturation;
                TxtSaturationVal.Text = frame.Saturation.ToString("0.0");

                SliderColorTemp.Value = frame.ColorTemp;
                TxtColorTempVal.Text = frame.ColorTemp.ToString("0");

                // ゴミ除去
                ChkDustRemoval.IsChecked = frame.DustRemovalEnabled;
                if (frame.DustRemovalStrength == 1) RadioDustWeak.IsChecked = true;
                else if (frame.DustRemovalStrength == 3) RadioDustStrong.IsChecked = true;
                else RadioDustNormal.IsChecked = true;

                // Exif (0や未設定時は空白で表示)
                ComboFNumber.Text = frame.FNumber > 0 ? frame.FNumber.ToString("0.#") : string.Empty;
                ComboShutterSpeed.Text = frame.ShutterSpeed;
                TxtFocalLength.Text = frame.FocalLength > 0 ? frame.FocalLength.ToString() : string.Empty;
                TxtExposureComp.Text = frame.ExposureCompensation;
                TxtFrameNotes.Text = frame.Notes;
            }
            finally
            {
                _isUpdatingUi = false;
            }
        }

        private void UpdatePositivePreview()
        {
            if (_selectedFrame == null || _currentScanMat == null || _currentScanMat.IsDisposed) return;

            try
            {
                var r = _selectedFrame.CropRect;
                int x = Math.Clamp(r.X, 0, _currentScanMat.Width - 1);
                int y = Math.Clamp(r.Y, 0, _currentScanMat.Height - 1);
                int w = Math.Clamp(r.Width, 1, _currentScanMat.Width - x);
                int h = Math.Clamp(r.Height, 1, _currentScanMat.Height - y);

                using var roi = _currentScanMat[new OpenCvSharp.Rect(x, y, w, h)];
                using var positiveMat = _negativeEngine.ConvertNegativeToPositive(roi, _selectedFrame);

                Mat finalMat = positiveMat;
                if (Math.Abs(_selectedFrame.RotationDegrees) > 0.1)
                {
                    int deg = ((int)Math.Round(_selectedFrame.RotationDegrees) % 360 + 360) % 360;
                    if (deg == 90) Cv2.Rotate(positiveMat, finalMat, RotateFlags.Rotate90Clockwise);
                    else if (deg == 180) Cv2.Rotate(positiveMat, finalMat, RotateFlags.Rotate180);
                    else if (deg == 270) Cv2.Rotate(positiveMat, finalMat, RotateFlags.Rotate90Counterclockwise);
                }

                ImgPositivePreview.Source = _negativeEngine.MatToBitmapSource(finalMat);
                UpdateFrameThumbnail(_selectedFrame);
            }
            catch { }
        }

        // --- UI イベントハンドラ ---

        private void MainCanvas_FrameSelected(object? sender, FilmFrame frame)
        {
            SelectFrame(frame);
        }

        private void MainCanvas_FrameModified(object? sender, EventArgs e)
        {
            if (_selectedFrame != null && _currentScanMat != null && !_currentScanMat.IsDisposed)
            {
                var r = _selectedFrame.CropRect;
                int x = Math.Clamp(r.X, 0, _currentScanMat.Width - 1);
                int y = Math.Clamp(r.Y, 0, _currentScanMat.Height - 1);
                int w = Math.Clamp(r.Width, 1, _currentScanMat.Width - x);
                int h = Math.Clamp(r.Height, 1, _currentScanMat.Height - y);

                using var roi = _currentScanMat[new OpenCvSharp.Rect(x, y, w, h)];
                _selectedFrame.RawImagePath = _sessionService.SaveFrameRawImage(_currentRoll.SessionId, _selectedFrame.Id, roi);

                UpdatePositivePreview();
            }
        }

        private void MainCanvas_ColorPicked(object? sender, (byte R, byte G, byte B) color)
        {
            if (_selectedFrame != null)
            {
                _selectedFrame.BaseColorR = color.R;
                _selectedFrame.BaseColorG = color.G;
                _selectedFrame.BaseColorB = color.B;

                UpdateRightPanelFromFrame(_selectedFrame);
                UpdatePositivePreview();
                TxtStatusMessage.Text = $"ベースカラーを取得: #{color.R:X2}{color.G:X2}{color.B:X2}";
            }
            ToggleEyedropper.IsChecked = false;
        }

        private void ListStrips_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingUi) return; // UI同期中はスキップ！

            if (ListStrips.SelectedItem is FilmStrip strip)
            {
                if (_currentStrip == strip && _currentScanMat != null && !_currentScanMat.IsDisposed)
                    return;

                _currentStrip = strip;
                if (!string.IsNullOrEmpty(strip.FullScanImagePath) && File.Exists(strip.FullScanImagePath))
                {
                    if (_currentScanMat != null && !_currentScanMat.IsDisposed)
                    {
                        _currentScanMat.Dispose();
                    }
                    _currentScanMat = Cv2.ImRead(strip.FullScanImagePath, ImreadModes.Color);
                    MainCanvas.ImageSource = _negativeEngine.MatToBitmapSource(_currentScanMat);
                    MainCanvas.Frames = strip.Frames;
                    MainCanvas.ResetView();

                    if (strip.Frames.Count > 0)
                        SelectFrame(strip.Frames[0]);
                }
            }
        }

        private void ListThumbnails_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ListThumbnails.SelectedItem is FilmFrame frame)
            {
                SelectFrame(frame);
            }
        }

        private void ComboFilmProfiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingUi || _selectedFrame == null) return;
            if (ComboFilmProfiles.SelectedValue is string profileId)
            {
                _selectedFrame.ProfileId = profileId;
                UpdatePositivePreview();
            }
        }

        private void SliderTuning_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingUi || _selectedFrame == null) return;

            _selectedFrame.Exposure = SliderExposure.Value;
            TxtExposureVal.Text = SliderExposure.Value.ToString("0.0");

            _selectedFrame.Contrast = SliderContrast.Value;
            TxtContrastVal.Text = SliderContrast.Value.ToString("0.0");

            _selectedFrame.Saturation = SliderSaturation.Value;
            TxtSaturationVal.Text = SliderSaturation.Value.ToString("0.0");

            _selectedFrame.ColorTemp = SliderColorTemp.Value;
            TxtColorTempVal.Text = SliderColorTemp.Value.ToString("0");

            UpdatePositivePreview();
        }

        private void BtnAutoDetectBase_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedFrame == null || _currentScanMat == null || _currentScanMat.IsDisposed) return;

            var baseCol = _negativeEngine.DetectBaseColor(_currentScanMat, _selectedFrame.CropRect);
            _selectedFrame.BaseColorR = baseCol.R;
            _selectedFrame.BaseColorG = baseCol.G;
            _selectedFrame.BaseColorB = baseCol.B;

            UpdateRightPanelFromFrame(_selectedFrame);
            UpdatePositivePreview();
            TxtStatusMessage.Text = $"ベースカラー自動検知: #{baseCol.R:X2}{baseCol.G:X2}{baseCol.B:X2}";
        }

        private void ChkDustRemoval_Checked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi || _selectedFrame == null) return;
            _selectedFrame.DustRemovalEnabled = ChkDustRemoval.IsChecked == true;
            UpdatePositivePreview();
        }

        private void RadioDust_Checked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi || _selectedFrame == null) return;
            if (RadioDustWeak.IsChecked == true) _selectedFrame.DustRemovalStrength = 1;
            else if (RadioDustStrong.IsChecked == true) _selectedFrame.DustRemovalStrength = 3;
            else _selectedFrame.DustRemovalStrength = 2;
            UpdatePositivePreview();
        }

        private void ExifField_Changed(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi || _selectedFrame == null) return;

            if (double.TryParse(ComboFNumber.Text, out double f)) _selectedFrame.FNumber = f;
            else _selectedFrame.FNumber = 0;

            _selectedFrame.ShutterSpeed = ComboShutterSpeed.Text.Trim();

            if (double.TryParse(TxtFocalLength.Text, out double fl)) _selectedFrame.FocalLength = fl;
            else _selectedFrame.FocalLength = 0;

            _selectedFrame.ExposureCompensation = TxtExposureComp.Text.Trim();
            _selectedFrame.Notes = TxtFrameNotes.Text;

            ListThumbnails.Items.Refresh();
        }

        private void BtnCopyPrevExif_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedFrame == null || _currentRoll.AllFrames.Count <= 1) return;

            int currIdx = _currentRoll.AllFrames.IndexOf(_selectedFrame);
            if (currIdx > 0)
            {
                var prev = _currentRoll.AllFrames[currIdx - 1];
                _selectedFrame.FNumber = prev.FNumber;
                _selectedFrame.ShutterSpeed = prev.ShutterSpeed;
                _selectedFrame.FocalLength = prev.FocalLength;
                _selectedFrame.ExposureCompensation = prev.ExposureCompensation;
                _selectedFrame.CameraMake = prev.CameraMake;
                _selectedFrame.CameraModel = prev.CameraModel;
                _selectedFrame.LensModel = prev.LensModel;
                _selectedFrame.ISO = prev.ISO;

                UpdateRightPanelFromFrame(_selectedFrame);
                ListThumbnails.Items.Refresh();
                TxtStatusMessage.Text = $"コマ #{prev.FrameNumber:D2} から Exif を複製しました。";
            }
        }

        private void BtnRotateCw_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedFrame == null) return;
            _selectedFrame.RotationDegrees = (_selectedFrame.RotationDegrees + 90) % 360;
            UpdatePositivePreview();
        }

        private void BtnRotateCcw_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedFrame == null) return;
            _selectedFrame.RotationDegrees = (_selectedFrame.RotationDegrees + 270) % 360;
            UpdatePositivePreview();
        }

        private void BtnFitView_Click(object sender, RoutedEventArgs e)
        {
            MainCanvas.ResetView();
        }

        private void ToggleEyedropper_Checked(object sender, RoutedEventArgs e)
        {
            MainCanvas.IsEyedropperMode = true;
            TxtStatusMessage.Text = "スポイトモード: プレビュー画面の未露光フィルム部分をクリックしてください";
        }

        private void ToggleEyedropper_Unchecked(object sender, RoutedEventArgs e)
        {
            MainCanvas.IsEyedropperMode = false;
        }

        private void BtnAddFrame_Click(object sender, RoutedEventArgs e)
        {
            if (_currentStrip == null || _currentScanMat == null || _currentScanMat.IsDisposed) return;

            var format = GetSelectedFormat();
            int w = (int)(_currentScanMat.Width * 0.15);
            int h = (int)(w / format.AspectRatio);
            int x = (_currentScanMat.Width - w) / 2;
            int y = (_currentScanMat.Height - h) / 2;

            var frame = new FilmFrame
            {
                StripId = _currentStrip.Id,
                FrameNumber = _currentRoll.AllFrames.Count + 1,
                CropRect = new OpenCvSharp.Rect(x, y, w, h),
                ProfileId = (ComboFilmProfiles.SelectedValue as string) ?? "portra400",
                CameraMake = string.Empty,
                CameraModel = TxtRollCamera.Text.Trim(),
                LensModel = TxtRollLens.Text.Trim(),
                ISO = _currentRoll.DefaultIso
            };

            using (var roi = _currentScanMat[frame.CropRect])
            {
                frame.RawImagePath = _sessionService.SaveFrameRawImage(_currentRoll.SessionId, frame.Id, roi);
            }

            UpdateFrameThumbnail(frame);
            _currentStrip.Frames.Add(frame);
            SyncAllFramesList();
            SelectFrame(frame);
        }

        private void BtnDeleteFrame_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedFrame == null || _currentStrip == null) return;

            _currentStrip.Frames.Remove(_selectedFrame);
            SyncAllFramesList();

            if (_currentStrip.Frames.Count > 0)
                SelectFrame(_currentStrip.Frames[0]);
            else
                _selectedFrame = null;

            MainCanvas.InvalidateVisual();
        }

        private void BtnAutoDetectFrames_Click(object sender, RoutedEventArgs e)
        {
            AutoDetectFramesForCurrentStrip();
        }

        private void ComboFilmFormat_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
        }

        private async void BtnRefreshScanner_Click(object sender, RoutedEventArgs e)
        {
            await RefreshScannersAsync();
        }

        private void BtnOpenImage_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "フィルムスキャン画像を開く",
                Filter = "画像ファイル (*.jpg;*.jpeg;*.tif;*.tiff;*.png;*.bmp)|*.jpg;*.jpeg;*.tif;*.tiff;*.png;*.bmp|すべてのファイル (*.*)|*.*"
            };

            if (dlg.ShowDialog() == true)
            {
                var mat = Cv2.ImRead(dlg.FileName, ImreadModes.Color);
                if (!mat.Empty())
                {
                    string name = Path.GetFileNameWithoutExtension(dlg.FileName);
                    ApplyNewScanData(name, mat, null, 2400, true);
                }
            }
        }

        private void TxtRollName_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_currentRoll != null)
            {
                _currentRoll.RollName = TxtRollName.Text;
            }
        }

        private void TxtRollFilmStock_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_currentRoll != null)
            {
                _currentRoll.FilmStock = TxtRollFilmStock.Text;
            }
        }

        // --- 一括エクスポート ---

        private async void BtnExportFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_currentRoll.AllFrames.Count == 0)
            {
                MessageBox.Show("エクスポートするコマがありません。スキャンまたはコマ割りを行ってください。", "書き出し", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new OpenFolderDialog
            {
                Title = "書き出し先フォルダを選択してください"
            };

            if (dlg.ShowDialog() == true)
            {
                string rollFolder = string.IsNullOrWhiteSpace(_currentRoll.RollName) ? "Roll_Export" : _currentRoll.RollName;
                string targetFolder = Path.Combine(dlg.FolderName, rollFolder);
                var options = new ExportOptions
                {
                    OutputDirectory = targetFolder,
                    Format = GetSelectedExportFormat(),
                    GenerateContactSheet = ChkContactSheet.IsChecked == true
                };

                await ExecuteExportAsync(async (prog) =>
                {
                    await _exportService.ExportToFolderAsync(_currentRoll, options, prog);
                }, $"フォルダへの一括書き出しが完了しました:\n{targetFolder}");
            }
        }

        private async void BtnExportZip_Click(object sender, RoutedEventArgs e)
        {
            if (_currentRoll.AllFrames.Count == 0)
            {
                MessageBox.Show("エクスポートするコマがありません。スキャンまたはコマ割りを行ってください。", "書き出し", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string defaultZipName = string.IsNullOrWhiteSpace(_currentRoll.RollName) ? "Roll_Export.zip" : $"{_currentRoll.RollName}.zip";

            var dlg = new SaveFileDialog
            {
                Title = "ロールZIPアーカイブの保存先",
                Filter = "ZIP アーカイブ (*.zip)|*.zip",
                FileName = defaultZipName
            };

            if (dlg.ShowDialog() == true)
            {
                var options = new ExportOptions
                {
                    OutputZipPath = dlg.FileName,
                    Format = GetSelectedExportFormat(),
                    GenerateContactSheet = ChkContactSheet.IsChecked == true
                };

                await ExecuteExportAsync(async (prog) =>
                {
                    await _exportService.ExportToZipAsync(_currentRoll, options, prog);
                }, $"ZIPアーカイブへの一括書き出しが完了しました:\n{dlg.FileName}");
            }
        }

        private string GetSelectedExportFormat()
        {
            if (ComboExportFormat.SelectedItem is ComboBoxItem item && item.Content != null)
            {
                string t = item.Content.ToString()!;
                if (t.Contains("TIFF")) return "TIFF";
                if (t.Contains("PNG")) return "PNG";
            }
            return "JPEG";
        }

        private async Task ExecuteExportAsync(Func<IProgress<(string msg, double pct)>, Task> exportAction, string successMsg)
        {
            BtnExportFolder.IsEnabled = false;
            BtnExportZip.IsEnabled = false;
            ProgressBarMain.Visibility = Visibility.Visible;
            ProgressBarMain.IsIndeterminate = false;

            var progress = new Progress<(string msg, double pct)>(update =>
            {
                TxtStatusMessage.Text = update.msg;
                ProgressBarMain.Value = update.pct * 100.0;
            });

            try
            {
                await exportAction(progress);
                MessageBox.Show(successMsg, "書き出し完了", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"書き出し中にエラーが発生しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnExportFolder.IsEnabled = true;
                BtnExportZip.IsEnabled = true;
                ProgressBarMain.Visibility = Visibility.Collapsed;
                ProgressBarMain.Value = 0;
                TxtStatusMessage.Text = "準備完了";
            }
        }

        private void MenuExit_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void MenuAbout_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(
                "IRIS PxS - Epson GT-X820 フィルムスキャニングシステム\n\n" +
                "バージョン: 1.2.0 (Professional Build)\n" +
                "対応機器: EPSON GT-X820 フラットベッドスキャナー\n" +
                "機能: 透過原稿スキャン (最大 12800 DPI), 赤外線ゴミ傷除去 (Digital ICE), 自動コマ検出, NP変換, ロール一括書き出し (Folder/ZIP)\n",
                "バージョン情報",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }
}