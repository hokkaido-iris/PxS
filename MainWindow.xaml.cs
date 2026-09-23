using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

            InitializeFormatControls();
            InitializeProfileCombo();
            InitializeDpiCombo();
            InitializeSession();

            ScanCanvas.FrameSelected += (s, frame) => SelectFrame(frame);
            ScanCanvas.FrameModified += (s, e) =>
            {
                if (_selectedFrame != null)
                {
                    UpdateFrameThumbnail(_selectedFrame);
                }
                else
                {
                    foreach (var f in _currentRoll.AllFrames)
                    {
                        UpdateFrameThumbnail(f);
                    }
                }
                SyncFrameControlPanelInputs();
                if (RbViewSingle.IsChecked == true) UpdateSingleFramePreview();
            };
            ScanCanvas.ColorPicked += (s, rgb) =>
            {
                ScanCanvas.IsEyedropperMode = false;
                BtnEyedropper.Background = (SolidColorBrush)FindResource("ControlLightGray");
                ApplyBaseColor(rgb.R, rgb.G, rgb.B);
                TxtStatus.Text = $"スポイト取得ベース色: R={rgb.R}, G={rgb.G}, B={rgb.B}";
            };

            Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await RefreshScannersAsync();
        }

        private void InitializeFormatControls()
        {
            CmbFilmSizeCategory.Items.Clear();
            CmbFilmSizeCategory.Items.Add("135 (35mm)");
            CmbFilmSizeCategory.Items.Add("120 (中判)");
            CmbFilmSizeCategory.Items.Add("127 (ベスト判)");
            CmbFilmSizeCategory.Items.Add("110 (ポケット)");
            CmbFilmSizeCategory.SelectedIndex = 0; // 135

            UpdateFormatSubList(FilmSizeCategory.Size135);
        }

        private void UpdateFormatSubList(FilmSizeCategory category)
        {
            var formats = FilmFormat.GetFormatsByCategory(category);
            CmbFilmFormat.ItemsSource = formats;
            CmbFilmFormat.DisplayMemberPath = nameof(FilmFormat.DisplayName);
            if (formats.Count > 0)
            {
                CmbFilmFormat.SelectedIndex = 0;
            }
        }

        private void InitializeProfileCombo()
        {
            // フィルム銘柄 ComboBox のプリセット候補
            CmbFilmBrand.Items.Clear();
            CmbFilmBrand.Items.Add("Kodak Portra 400");
            CmbFilmBrand.Items.Add("Kodak Portra 160");
            CmbFilmBrand.Items.Add("Kodak Gold 200");
            CmbFilmBrand.Items.Add("Kodak UltraMax 400");
            CmbFilmBrand.Items.Add("Kodak Tri-X 400");
            CmbFilmBrand.Items.Add("Fujifilm Pro 400H");
            CmbFilmBrand.Items.Add("Fujifilm Superia Premium 400");
            CmbFilmBrand.Items.Add("Fujifilm Acros II 100");
            CmbFilmBrand.Items.Add("CineStill 800T");
            CmbFilmBrand.Items.Add("Ilford HP5 Plus 400");
            CmbFilmBrand.Text = string.Empty; // デフォルト空白

            // ISO感度 ComboBox
            CmbRollIso.Items.Clear();
            CmbRollIso.Items.Add("50");
            CmbRollIso.Items.Add("100");
            CmbRollIso.Items.Add("160");
            CmbRollIso.Items.Add("200");
            CmbRollIso.Items.Add("400");
            CmbRollIso.Items.Add("800");
            CmbRollIso.Items.Add("1600");
            CmbRollIso.Items.Add("3200");
            CmbRollIso.Text = string.Empty; // デフォルト空白
        }

        private void InitializeDpiCombo()
        {
            CmbDpi.Items.Clear();
            CmbDpi.Items.Add("300 DPI (Pre-Scan用)");
            CmbDpi.Items.Add("600 DPI");
            CmbDpi.Items.Add("1200 DPI");
            CmbDpi.Items.Add("2400 DPI (標準)");
            CmbDpi.Items.Add("3200 DPI (高精細)");
            CmbDpi.Items.Add("4800 DPI");
            CmbDpi.Items.Add("6400 DPI (光学最高)");
            CmbDpi.Items.Add("9600 DPI (高品位補間)");
            CmbDpi.Items.Add("12800 DPI (最大補間)");
            CmbDpi.SelectedIndex = 3; // 2400 DPI
        }

        private void InitializeSession()
        {
            _currentRoll = new RollSession();

            // デフォルトはすべて完全空白
            TxtRollName.Text = string.Empty;
            CmbFilmBrand.Text = string.Empty;
            CmbRollIso.Text = string.Empty;

            LstFilmStrip.ItemsSource = _currentRoll.AllFrames;
            DgFramesTable.ItemsSource = _currentRoll.AllFrames;
            UpdateFrameSummary();
        }

        private async Task RefreshScannersAsync()
        {
            SetScannerStatus(ScannerState.Busy, "スキャナー検索中...");
            TxtStatus.Text = "スキャナーを検索しています...";

            var scanners = await _scannerService.GetConnectedScannersAsync();
            CmbScannerList.Items.Clear();

            foreach (var sc in scanners)
            {
                CmbScannerList.Items.Add(sc.Name);
            }

            var gtx = scanners.FirstOrDefault(s => s.IsEpsonGtx820) ?? scanners.FirstOrDefault();

            if (gtx != null)
            {
                _activeScanner = gtx;
                CmbScannerList.SelectedItem = gtx.Name;
                SetScannerStatus(ScannerState.Ready, $"{gtx.Name} (Ready)");
                TxtStatus.Text = "EPSON GT-X820 が検出されました。フィルムをセットして [Pre-Scan] または [Scan] を実行してください。";
            }
            else
            {
                _activeScanner = new ScannerDeviceInfo { Name = "EPSON GT-X820", IsConnected = false };
                CmbScannerList.Items.Add("EPSON GT-X820 (未検出)");
                CmbScannerList.SelectedIndex = 0;
                SetScannerStatus(ScannerState.Disconnected, "未検出");
                TxtStatus.Text = "GT-X820 が見つかりません。USBケーブルと電源を確認してください。";
            }
        }

        private enum ScannerState { Ready, Busy, Disconnected }

        private void SetScannerStatus(ScannerState state, string message)
        {
            switch (state)
            {
                case ScannerState.Ready:
                    LedScannerStatus.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(40, 167, 69)); // #28A745
                    LedScannerStatus.Stroke = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 126, 52));
                    TxtScannerStatus.Text = "Ready";
                    TxtScannerStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(40, 167, 69));
                    break;
                case ScannerState.Busy:
                    LedScannerStatus.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(253, 126, 20)); // #FD7E14
                    LedScannerStatus.Stroke = new SolidColorBrush(System.Windows.Media.Color.FromRgb(211, 84, 0));
                    TxtScannerStatus.Text = "Busy";
                    TxtScannerStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(253, 126, 20));
                    break;
                case ScannerState.Disconnected:
                    LedScannerStatus.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 53, 69)); // #DC3545
                    LedScannerStatus.Stroke = new SolidColorBrush(System.Windows.Media.Color.FromRgb(189, 33, 48));
                    TxtScannerStatus.Text = "Offline";
                    TxtScannerStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 53, 69));
                    break;
            }
        }

        /// <summary>
        /// 現在メモリ上に展開されているスキャン画像の実際の解像度（DPI）を取得する
        /// </summary>
        private int GetCurrentScanMatDpi()
        {
            // 1. カレントストリップのScanDpiがあればそれを最優先
            if (_currentStrip != null && _currentStrip.ScanDpi > 0)
            {
                return _currentStrip.ScanDpi;
            }

            // 2. 画像サイズとGT-X820透過原稿エリアから実効DPIを自動推定
            if (_currentScanMat != null && !_currentScanMat.IsDisposed)
            {
                int maxDim = Math.Max(_currentScanMat.Width, _currentScanMat.Height);
                double estDpi = maxDim / (240.0 / 25.4);
                int[] standardDpis = { 300, 600, 1200, 2400, 3200, 4800, 6400, 9600, 12800 };
                return standardDpis.OrderBy(d => Math.Abs(d - estDpi)).First();
            }

            return 300;
        }

        private int GetSelectedScanDpi()
        {
            if (CmbDpi.SelectedItem is string text)
            {
                if (text.Contains("12800")) return 12800;
                if (text.Contains("9600")) return 9600;
                if (text.Contains("6400")) return 6400;
                if (text.Contains("4800")) return 4800;
                if (text.Contains("3200")) return 3200;
                if (text.Contains("2400")) return 2400;
                if (text.Contains("1200")) return 1200;
                if (text.Contains("600")) return 600;
                if (text.Contains("300")) return 300;
            }
            return 2400;
        }

        private FilmFormat GetSelectedFormat()
        {
            return (CmbFilmFormat.SelectedItem as FilmFormat) ?? FilmFormat.GetPresetFormats()[0];
        }

        // ======================================================================
        // 【上側】Machine Control イベントハンドラ
        // ======================================================================

        private async void BtnPreScan_Click(object sender, RoutedEventArgs e)
        {
            await RunScanAsync(300, isPreScan: true);
        }

        private async void BtnScanCurrent_Click(object sender, RoutedEventArgs e)
        {
            int dpi = GetSelectedScanDpi();
            await RunScanAsync(dpi, isPreScan: false);
        }

        private async void BtnScanDialog_Click(object sender, RoutedEventArgs e)
        {
            SetScanningUiState(true);
            var progress = new Progress<string>(msg => TxtStatus.Text = msg);

            try
            {
                var (colorMat, irMat) = await _scannerService.ScanWithDialogAsync(progress);
                string stripName = $"Strip {_currentRoll.Strips.Count + 1}";
                ApplyNewScanData(stripName, colorMat, irMat, 2400, true);
            }
            catch (OperationCanceledException)
            {
                TxtStatus.Text = "スキャンをキャンセルしました。";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"スキャン中にエラーが発生しました: {ex.Message}", "スキャンエラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetScanningUiState(false);
            }
        }

        private async Task RunScanAsync(int dpi, bool isPreScan)
        {
            SetScanningUiState(true);
            var progress = new Progress<string>(msg => TxtStatus.Text = msg);

            try
            {
                // フィルムスキャンのため isTransmissive = true (TPU 透過光ユニット点灯)
                var (colorMat, irMat) = await _scannerService.ScanAsync(_activeScanner, dpi, true, null, progress);

                string stripName = isPreScan ? $"PreScan_{DateTime.Now:HHmmss}" : $"Strip {_currentRoll.Strips.Count + 1}";
                ApplyNewScanData(stripName, colorMat, irMat, dpi, true);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"スキャン中にエラーが発生しました: {ex.Message}", "スキャンエラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetScanningUiState(false);
            }
        }

        private void SetScanningUiState(bool isScanning)
        {
            BtnPreScan.IsEnabled = !isScanning;
            BtnScan.IsEnabled = !isScanning;
            BtnScannerSetting.IsEnabled = !isScanning;
            PrgScan.Visibility = isScanning ? Visibility.Visible : Visibility.Collapsed;
            PrgScan.IsIndeterminate = isScanning;
            SetScannerStatus(isScanning ? ScannerState.Busy : ScannerState.Ready, isScanning ? "Scanning..." : "Ready");
        }

        private void ApplyNewScanData(string stripName, Mat colorMat, Mat? irMat, int dpi, bool autoDetect)
        {
            _isUpdatingUi = true;
            try
            {
                if (_currentScanMat != null && !_currentScanMat.IsDisposed)
                {
                    _currentScanMat.Dispose();
                }
                if (_currentIrMat != null && !_currentIrMat.IsDisposed)
                {
                    _currentIrMat.Dispose();
                }

                _currentScanMat = colorMat.Clone();
                _currentIrMat = irMat?.Clone();

                var strip = new FilmStrip
                {
                    Name = stripName,
                    StripIndex = _currentRoll.Strips.Count + 1,
                    ScanDpi = dpi
                };

                var (rawPath, irPath) = _sessionService.SaveStripImages(_currentRoll.SessionId, strip.Id, _currentScanMat, _currentIrMat);
                strip.FullScanImagePath = rawPath;
                strip.FullScanIrPath = irPath;

                _currentRoll.Strips.Add(strip);
                _currentStrip = strip;

                // スキャン原稿画像を表示
                var wpfBitmap = _currentScanMat.ToBitmapSource();
                ScanCanvas.ImageSource = wpfBitmap;
                ScanCanvas.Frames = _currentRoll.AllFrames;

                if (autoDetect)
                {
                    PerformAutoDetectFrames();
                }
            }
            finally
            {
                _isUpdatingUi = false;
            }

            TxtStatus.Text = $"スキャン完了: {colorMat.Width}x{colorMat.Height} px ({dpi} DPI)";
            UpdateFrameSummary();
        }

        // ======================================================================
        // 【フィルム種別】Color/B&W, Negative/Positive
        // ======================================================================

        private void FilmType_Changed(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi) return;

            bool isColor = RbColor.IsChecked == true;
            bool isNeg = RbNegative.IsChecked == true;

            _currentRoll.IsColor = isColor;
            _currentRoll.IsNegative = isNeg;

            // 全コマにフィルム種別を反映
            foreach (var f in _currentRoll.AllFrames)
            {
                f.IsColor = isColor;
                f.IsNegative = isNeg;
                UpdateFrameThumbnail(f);
            }

            if (RbViewSingle.IsChecked == true)
            {
                UpdateSingleFramePreview();
            }

            TxtStatus.Text = $"フィルム種別変更: {(isColor ? "Color" : "B&W")} / {(isNeg ? "Negative" : "Positive")}";
        }

        // ======================================================================
        // 【中央】Control Panel ビュー切替 & 表示制御
        // ======================================================================

        private void ViewMode_Changed(object sender, RoutedEventArgs e)
        {
            if (RbViewFull.IsChecked == true)
            {
                GridFullView.Visibility = Visibility.Visible;
                GridSingleView.Visibility = Visibility.Collapsed;
                GridTableView.Visibility = Visibility.Collapsed;
                GridIceDiffView.Visibility = Visibility.Collapsed;
                TxtViewModeTitle.Text = "スキャン全体ビュー";
            }
            else if (RbViewSingle.IsChecked == true)
            {
                GridFullView.Visibility = Visibility.Collapsed;
                GridSingleView.Visibility = Visibility.Visible;
                GridTableView.Visibility = Visibility.Collapsed;
                GridIceDiffView.Visibility = Visibility.Collapsed;
                TxtViewModeTitle.Text = "コマ個別ビュー";
                UpdateSingleFramePreview();
            }
            else if (RbViewTable.IsChecked == true)
            {
                GridFullView.Visibility = Visibility.Collapsed;
                GridSingleView.Visibility = Visibility.Collapsed;
                GridTableView.Visibility = Visibility.Visible;
                GridIceDiffView.Visibility = Visibility.Collapsed;
                TxtViewModeTitle.Text = "全コマ設定表";
                DgFramesTable.ItemsSource = null;
                DgFramesTable.ItemsSource = _currentRoll.AllFrames;
            }
            else if (RbViewIce.IsChecked == true)
            {
                GridFullView.Visibility = Visibility.Collapsed;
                GridSingleView.Visibility = Visibility.Collapsed;
                GridTableView.Visibility = Visibility.Collapsed;
                GridIceDiffView.Visibility = Visibility.Visible;
                TxtViewModeTitle.Text = "ICE 赤外線差分マップビュー";
                UpdateIceDiffPreview();
            }
        }

        private void BtnShowIceDiff_Click(object sender, RoutedEventArgs e)
        {
            RbViewIce.IsChecked = true;
            ViewMode_Changed(sender, e);
        }

        private void UpdateIceDiffPreview()
        {
            if (_currentScanMat == null || _currentScanMat.IsDisposed)
            {
                ImgIceDiffPreview.Source = null;
                return;
            }

            try
            {
                using var diffMat = _currentIrMat != null && !_currentIrMat.IsDisposed
                    ? _dustService.GenerateDefectMaskFromIr(_currentIrMat, 2)
                    : _dustService.GenerateDefectMaskFromColor(_currentScanMat, 2);

                // 差分マスクを見やすくするため、黒背景にネオンシアン(0, 255, 255)でハイライト表示
                using var colorDiff = new Mat();
                Cv2.CvtColor(diffMat, colorDiff, ColorConversionCodes.GRAY2BGR);

                using var coloredMask = new Mat(diffMat.Size(), MatType.CV_8UC3, new Scalar(255, 255, 0)); // BGR: Cyan
                using var finalDiff = new Mat();
                Cv2.BitwiseAnd(coloredMask, coloredMask, finalDiff, diffMat);

                ImgIceDiffPreview.Source = finalDiff.ToBitmapSource();
                TxtStatus.Text = "ICE 赤外線ゴミ・キズ差分マップを表示中";
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"UpdateIceDiffPreview error: {ex.Message}");
            }
        }

        private void UpdateSingleFramePreview()
        {
            if (_selectedFrame == null || _currentScanMat == null || _currentScanMat.IsDisposed)
            {
                ImgSinglePreview.Source = null;
                TxtSingleFrameInfo.Text = "コマ未選択";
                return;
            }

            int index = _currentRoll.AllFrames.IndexOf(_selectedFrame);
            TxtSingleFrameInfo.Text = $"コマ {index + 1} / {_currentRoll.AllFrames.Count} (Frame #{_selectedFrame.FrameNumber})";

            // ネガポジ反転・カラー補正画像をレンダリング
            try
            {
                var crop = _selectedFrame.GetInsetCropRect();
                if (crop.Width <= 0 || crop.Height <= 0) return;

                // 領域クリップ
                int x = Math.Max(0, Math.Min(crop.X, _currentScanMat.Width - 1));
                int y = Math.Max(0, Math.Min(crop.Y, _currentScanMat.Height - 1));
                int w = Math.Min(crop.Width, _currentScanMat.Width - x);
                int h = Math.Min(crop.Height, _currentScanMat.Height - y);

                using var croppedMat = new Mat(_currentScanMat, new OpenCvSharp.Rect(x, y, w, h));
                using var invertedMat = _negativeEngine.ConvertNegativeToPositive(croppedMat, _selectedFrame);

                // 回転適用
                using var rotatedMat = ApplyRotation(invertedMat, _selectedFrame.RotationDegrees);

                ImgSinglePreview.Source = rotatedMat.ToBitmapSource();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"UpdateSingleFramePreview error: {ex.Message}");
            }
        }

        private static Mat ApplyRotation(Mat src, double degrees)
        {
            int d = ((int)Math.Round(degrees) % 360 + 360) % 360;
            var dst = new Mat();
            switch (d)
            {
                case 90:
                    Cv2.Rotate(src, dst, RotateFlags.Rotate90Clockwise);
                    return dst;
                case 180:
                    Cv2.Rotate(src, dst, RotateFlags.Rotate180);
                    return dst;
                case 270:
                    Cv2.Rotate(src, dst, RotateFlags.Rotate90Counterclockwise);
                    return dst;
                default:
                    return src.Clone();
            }
        }

        private void BtnPrevFrame_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedFrame == null || _currentRoll.AllFrames.Count == 0) return;
            int idx = _currentRoll.AllFrames.IndexOf(_selectedFrame);
            if (idx > 0)
            {
                SelectFrame(_currentRoll.AllFrames[idx - 1]);
            }
        }

        private void BtnNextFrame_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedFrame == null || _currentRoll.AllFrames.Count == 0) return;
            int idx = _currentRoll.AllFrames.IndexOf(_selectedFrame);
            if (idx < _currentRoll.AllFrames.Count - 1)
            {
                SelectFrame(_currentRoll.AllFrames[idx + 1]);
            }
        }

        private void BtnZoomFit_Click(object sender, RoutedEventArgs e)
        {
            ScanCanvas.ResetView();
        }

        private void BtnRotateFrame_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedFrame != null)
            {
                _selectedFrame.RotationDegrees = (_selectedFrame.RotationDegrees + 90.0) % 360.0;
                UpdateFrameThumbnail(_selectedFrame);
                if (RbViewSingle.IsChecked == true)
                {
                    UpdateSingleFramePreview();
                }
            }
        }

        // ======================================================================
        // 【中央】全コマ設定表 (DataGrid) 一括操作
        // ======================================================================

        private void BtnApplySelectedToAll_Click(object sender, RoutedEventArgs e)
        {
            var target = DgFramesTable.SelectedItem as FilmFrame ?? _selectedFrame;
            if (target == null)
            {
                MessageBox.Show("コピー元の行（コマ）を選択してください。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var result = MessageBox.Show(
                $"コマ #{target.FrameNumber} のカメラ「{target.CameraModel}」、レンズ「{target.LensModel}」、F値「{target.FNumber}」、SS「{target.ShutterSpeed}」、ISO「{target.ISO}」を全コマにコピーしますか？",
                "全コマ一括適用", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                foreach (var f in _currentRoll.AllFrames)
                {
                    f.CameraModel = target.CameraModel;
                    f.CameraMake = target.CameraMake;
                    f.LensModel = target.LensModel;
                    f.FNumber = target.FNumber;
                    f.ShutterSpeed = target.ShutterSpeed;
                    f.ISO = target.ISO;
                    f.ExposureCompensation = target.ExposureCompensation;
                }
                DgFramesTable.Items.Refresh();
                LstFilmStrip.Items.Refresh();
                TxtStatus.Text = "全コマにメタデータを一括適用しました。";
            }
        }

        private void BtnRenumberFrames_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("すべてのコマ番号を 1 から順に連番で再採番しますか？", "コマ番号再採番", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                for (int i = 0; i < _currentRoll.AllFrames.Count; i++)
                {
                    _currentRoll.AllFrames[i].FrameNumber = i + 1;
                }
                DgFramesTable.Items.Refresh();
                LstFilmStrip.Items.Refresh();
                TxtStatus.Text = "コマ番号を再採番しました。";
            }
        }

        private void DgFramesTable_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DgFramesTable.SelectedItem is FilmFrame frame && frame != _selectedFrame)
            {
                SelectFrame(frame);
            }
        }

        // ======================================================================
        // 【左側】Data Control イベントハンドラ
        // ======================================================================

        private void CmbFilmSizeCategory_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbFilmSizeCategory.SelectedIndex < 0) return;
            var cat = (FilmSizeCategory)CmbFilmSizeCategory.SelectedIndex;
            UpdateFormatSubList(cat);
        }

        private void CmbFilmFormat_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingUi) return;
            if (_currentScanMat != null && !_currentScanMat.IsDisposed)
            {
                PerformAutoDetectFrames();
            }
        }

        private void CmbDpi_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
        }

        private void CmbScannerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbScannerList.SelectedItem is string name && _scannerService != null)
            {
                // 選択スキャナー切り替え
            }
        }

        private void TxtRollName_TextChanged(object sender, TextChangedEventArgs e)
        {
            _currentRoll.RollName = TxtRollName.Text;
        }

        private void CmbFilmBrand_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbFilmBrand.SelectedItem is string brand)
            {
                _currentRoll.FilmStock = brand;
            }
        }

        private void CmbRollIso_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbRollIso.SelectedItem is string isoStr && int.TryParse(isoStr, out int iso))
            {
                _currentRoll.DefaultIso = iso;
            }
        }

        private void BtnAutoDetectFrames_Click(object sender, RoutedEventArgs e)
        {
            PerformAutoDetectFrames();
        }

        private void PerformAutoDetectFrames()
        {
            if (_currentScanMat == null || _currentScanMat.IsDisposed)
            {
                MessageBox.Show("スキャン画像または読み込み画像がありません。[Pre-Scan] を実行してください。", "案内", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var format = GetSelectedFormat();
            int dpi = GetCurrentScanMatDpi();
            TxtStatus.Text = $"フィルム全体のコントラストから傾き検知およびコマ枠配置を実行中 (フォーマット: {format.DisplayName}, {dpi} DPI)...";

            var (straightenedMat, skewAngle, detectedRects) = _detectorService.DetectAndStraighten(_currentScanMat, format, dpi);

            // 傾きが検知された場合 (0.1度以上)、画像を正立（回転補正）した画像に差し替え
            if (Math.Abs(skewAngle) >= 0.1)
            {
                _currentScanMat.Dispose();
                _currentScanMat = straightenedMat;

                if (_currentIrMat != null && !_currentIrMat.IsDisposed)
                {
                    var straightIr = _detectorService.StraightenImage(_currentIrMat, skewAngle);
                    _currentIrMat.Dispose();
                    _currentIrMat = straightIr;
                }

                if (_currentStrip != null)
                {
                    var (rawPath, irPath) = _sessionService.SaveStripImages(_currentRoll.SessionId, _currentStrip.Id, _currentScanMat, _currentIrMat);
                    _currentStrip.FullScanImagePath = rawPath;
                    _currentStrip.FullScanIrPath = irPath;
                }

                ScanCanvas.ImageSource = _currentScanMat.ToBitmapSource();
            }
            else
            {
                straightenedMat.Dispose();
            }

            if (_currentStrip == null)
            {
                _currentStrip = new FilmStrip { Name = "Strip 1", ScanDpi = dpi };
                _currentRoll.Strips.Add(_currentStrip);
            }

            _currentStrip.Frames.Clear();
            _currentRoll.AllFrames.Clear();

            int startNumber = 1;
            foreach (var r in detectedRects)
            {
                var frame = new FilmFrame
                {
                    FrameNumber = startNumber++,
                    StripId = _currentStrip.Id,
                    CropRect = r,
                    RawImagePath = _currentStrip.FullScanImagePath,
                    IrImagePath = _currentStrip.FullScanIrPath,
                    CameraModel = "",
                    LensModel = "",
                    FNumber = 0.0,
                    ShutterSpeed = "",
                    ISO = _currentRoll.DefaultIso,
                    IsColor = _currentRoll.IsColor,
                    IsNegative = _currentRoll.IsNegative,
                    CropInsetPercent = _currentRoll.DefaultCropInsetPercent
                };

                // ベースカラー初期値
                if (_selectedFrame != null)
                {
                    frame.BaseColorR = _selectedFrame.BaseColorR;
                    frame.BaseColorG = _selectedFrame.BaseColorG;
                    frame.BaseColorB = _selectedFrame.BaseColorB;
                }

                _currentStrip.Frames.Add(frame);
                _currentRoll.AllFrames.Add(frame);
                UpdateFrameThumbnail(frame);
            }

            ScanCanvas.Frames = null;
            ScanCanvas.Frames = _currentRoll.AllFrames;
            ScanCanvas.InvalidateVisual();

            LstFilmStrip.ItemsSource = null;
            LstFilmStrip.ItemsSource = _currentRoll.AllFrames;

            DgFramesTable.ItemsSource = null;
            DgFramesTable.ItemsSource = _currentRoll.AllFrames;

            if (_currentRoll.AllFrames.Count > 0)
            {
                SelectFrame(_currentRoll.AllFrames[0]);
            }

            UpdateFrameSummary();
            TxtStatus.Text = Math.Abs(skewAngle) >= 0.1
                ? $"コントラストから傾き {skewAngle:F1}° を検知・自動正立補正し、フォーマット「{format.DisplayName}」に基づき {detectedRects.Count} コマを自動生成しました。"
                : $"フォーマット「{format.DisplayName}」に基づき {detectedRects.Count} コマを自動配置しました。";
        }

        private void BtnAddFrame_Click(object sender, RoutedEventArgs e)
        {
            if (_currentScanMat == null || _currentScanMat.IsDisposed)
            {
                MessageBox.Show("スキャン画像がありません。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_currentStrip == null)
            {
                _currentStrip = new FilmStrip { Name = "Strip 1", ScanDpi = GetCurrentScanMatDpi() };
                _currentRoll.Strips.Add(_currentStrip);
            }

            var format = GetSelectedFormat();
            int dpi = GetCurrentScanMatDpi();
            bool isVertical = _currentScanMat.Height >= _currentScanMat.Width;
            FrameDetectorService.GetFormatDimensions(format, isVertical, dpi, out int defaultW, out int defaultH, out _);

            int startX = Math.Max(0, (_currentScanMat.Width - defaultW) / 2);
            int startY = Math.Max(0, (_currentScanMat.Height - defaultH) / 2);

            int nextNum = _currentRoll.AllFrames.Count > 0 ? _currentRoll.AllFrames.Max(f => f.FrameNumber) + 1 : 1;
            var newFrame = new FilmFrame
            {
                FrameNumber = nextNum,
                StripId = _currentStrip.Id,
                CropRect = new OpenCvSharp.Rect(startX, startY, defaultW, defaultH),
                RawImagePath = _currentStrip.FullScanImagePath,
                IrImagePath = _currentStrip.FullScanIrPath,
                CameraModel = "",
                LensModel = "",
                IsColor = _currentRoll.IsColor,
                IsNegative = _currentRoll.IsNegative,
                CropInsetPercent = _currentRoll.DefaultCropInsetPercent
            };

            _currentStrip.Frames.Add(newFrame);
            _currentRoll.AllFrames.Add(newFrame);

            UpdateFrameThumbnail(newFrame);
            ScanCanvas.Frames = null;
            ScanCanvas.Frames = _currentRoll.AllFrames;
            ScanCanvas.InvalidateVisual();

            LstFilmStrip.ItemsSource = null;
            LstFilmStrip.ItemsSource = _currentRoll.AllFrames;

            DgFramesTable.ItemsSource = null;
            DgFramesTable.ItemsSource = _currentRoll.AllFrames;

            SelectFrame(newFrame);
            UpdateFrameSummary();
        }

        private void BtnDeleteFrame_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedFrame != null && _currentStrip != null)
            {
                _currentStrip.Frames.Remove(_selectedFrame);
                _currentRoll.AllFrames.Remove(_selectedFrame);
                ScanCanvas.Frames = _currentRoll.AllFrames;
                ScanCanvas.InvalidateVisual();

                if (_currentRoll.AllFrames.Count > 0)
                {
                    SelectFrame(_currentRoll.AllFrames[0]);
                }
                else
                {
                    _selectedFrame = null;
                }
                UpdateFrameSummary();
            }
        }

        private void BtnNudgeUp_Click(object sender, RoutedEventArgs e)
        {
            NudgeFrames(isForward: false);
        }

        private void BtnNudgeDown_Click(object sender, RoutedEventArgs e)
        {
            NudgeFrames(isForward: true);
        }

        private void NudgeFrames(bool isForward)
        {
            if (_currentRoll.AllFrames.Count == 0) return;

            int step = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift) ? 10 : 2;
            int delta = isForward ? step : -step;

            bool isVertical = _currentScanMat == null || _currentScanMat.Height >= _currentScanMat.Width;
            int dx = isVertical ? 0 : delta;
            int dy = isVertical ? delta : 0;

            // ユーザー要望: コマ位置微調整は適用されている全コマを同時に移動
            ScanCanvas.NudgeAllFrames(dx, dy);
            foreach (var f in _currentRoll.AllFrames)
            {
                UpdateFrameThumbnail(f);
            }
            SyncFrameControlPanelInputs();
            if (RbViewSingle?.IsChecked == true) UpdateSingleFramePreview();
        }

        // ======================================================================
        // 【中央】選択コマ精密制御パネル (D-Pad, ステップ, サイズ, 数値入力, ドラッグロック)
        // ======================================================================

        private int GetSelectedStep()
        {
            if (RbStep1?.IsChecked == true) return 1;
            if (RbStep5?.IsChecked == true) return 5;
            if (RbStep10?.IsChecked == true) return 10;
            if (RbStep50?.IsChecked == true) return 50;
            return 1;
        }

        private void BtnFrameLeft_Click(object sender, RoutedEventArgs e) => MoveFrameByDelta(-GetSelectedStep(), 0);
        private void BtnFrameRight_Click(object sender, RoutedEventArgs e) => MoveFrameByDelta(GetSelectedStep(), 0);
        private void BtnFrameUp_Click(object sender, RoutedEventArgs e) => MoveFrameByDelta(0, -GetSelectedStep());
        private void BtnFrameDown_Click(object sender, RoutedEventArgs e) => MoveFrameByDelta(0, GetSelectedStep());

        private void MoveFrameByDelta(int dx, int dy)
        {
            if (_selectedFrame == null && (_currentRoll.AllFrames == null || _currentRoll.AllFrames.Count == 0)) return;

            if (RbTargetAllFrames?.IsChecked == true)
            {
                ScanCanvas.NudgeAllFrames(dx, dy);
                foreach (var f in _currentRoll.AllFrames)
                {
                    UpdateFrameThumbnail(f);
                }
            }
            else
            {
                ScanCanvas.NudgeSelectedFrame(dx, dy);
                if (_selectedFrame != null)
                {
                    UpdateFrameThumbnail(_selectedFrame);
                }
            }

            SyncFrameControlPanelInputs();
            if (RbViewSingle?.IsChecked == true) UpdateSingleFramePreview();
        }

        private void BtnFrameWidthDec_Click(object sender, RoutedEventArgs e) => ResizeFrameByDelta(-GetSelectedStep(), 0);
        private void BtnFrameWidthInc_Click(object sender, RoutedEventArgs e) => ResizeFrameByDelta(GetSelectedStep(), 0);
        private void BtnFrameHeightDec_Click(object sender, RoutedEventArgs e) => ResizeFrameByDelta(0, -GetSelectedStep());
        private void BtnFrameHeightInc_Click(object sender, RoutedEventArgs e) => ResizeFrameByDelta(0, GetSelectedStep());

        private void ResizeFrameByDelta(int dw, int dh)
        {
            if (_selectedFrame == null) return;
            ScanCanvas.ResizeSelectedFrame(dw, dh);
            UpdateFrameThumbnail(_selectedFrame);
            SyncFrameControlPanelInputs();
            if (RbViewSingle?.IsChecked == true) UpdateSingleFramePreview();
        }

        private void TxtFrameCoord_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                CommitFrameCoordInputs();
                e.Handled = true;
            }
        }

        private void TxtFrameCoord_LostFocus(object sender, RoutedEventArgs e)
        {
            CommitFrameCoordInputs();
        }

        private void CommitFrameCoordInputs()
        {
            if (_isUpdatingUi || _selectedFrame == null) return;

            if (int.TryParse(TxtFrameX.Text, out int x) &&
                int.TryParse(TxtFrameY.Text, out int y) &&
                int.TryParse(TxtFrameW.Text, out int w) &&
                int.TryParse(TxtFrameH.Text, out int h))
            {
                ScanCanvas.SetSelectedFrameRect(x, y, w, h);
                UpdateFrameThumbnail(_selectedFrame);
                SyncFrameControlPanelInputs();
                if (RbViewSingle?.IsChecked == true) UpdateSingleFramePreview();
            }
            else
            {
                SyncFrameControlPanelInputs();
            }
        }

        private void SyncFrameControlPanelInputs()
        {
            if (TxtSelectedFrameBadge == null || TxtFrameX == null) return;

            if (_selectedFrame == null)
            {
                TxtSelectedFrameBadge.Text = "コマ未選択";
                BadgeFrameSelection.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(140, 140, 140));
                TxtFrameX.Text = "";
                TxtFrameY.Text = "";
                TxtFrameW.Text = "";
                TxtFrameH.Text = "";
                return;
            }

            _isUpdatingUi = true;
            try
            {
                TxtSelectedFrameBadge.Text = $"コマ #{_selectedFrame.FrameNumber}";
                BadgeFrameSelection.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 120, 215));
                var r = _selectedFrame.CropRect;
                TxtFrameX.Text = r.X.ToString();
                TxtFrameY.Text = r.Y.ToString();
                TxtFrameW.Text = r.Width.ToString();
                TxtFrameH.Text = r.Height.ToString();
            }
            finally
            {
                _isUpdatingUi = false;
            }
        }

        private void ChkLockMouseDrag_Changed(object sender, RoutedEventArgs e)
        {
            if (ScanCanvas != null && ChkLockMouseDrag != null)
            {
                ScanCanvas.IsDragEnabled = (ChkLockMouseDrag.IsChecked == false);
            }
        }

        private void SliderCropInset_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtCropInsetValue == null || _currentRoll == null || ScanCanvas == null) return;
            double val = Math.Round(e.NewValue, 1);
            TxtCropInsetValue.Text = $"{val:0.0} %";

            _currentRoll.DefaultCropInsetPercent = val;

            if (_currentRoll.AllFrames != null)
            {
                foreach (var frame in _currentRoll.AllFrames)
                {
                    frame.CropInsetPercent = val;
                    UpdateFrameThumbnail(frame);
                }
            }

            ScanCanvas.InvalidateVisual();
            if (RbViewSingle?.IsChecked == true)
            {
                UpdateSingleFramePreview();
            }
        }

        // ======================================================================
        // 【右側】Image Control (カラー補正 & ICE)
        // ======================================================================

        private void BtnAutoBaseColor_Click(object sender, RoutedEventArgs e)
        {
            if (_currentScanMat == null || _currentScanMat.IsDisposed || _selectedFrame == null)
            {
                MessageBox.Show("コマ枠を選択してください。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var detectedColor = _negativeEngine.DetectBaseColor(_currentScanMat, _selectedFrame.CropRect);
            ApplyBaseColor(detectedColor.R, detectedColor.G, detectedColor.B);
            TxtStatus.Text = $"ベースカラーを自動検知しました (R:{detectedColor.R} G:{detectedColor.G} B:{detectedColor.B})";
        }

        private void BtnEyedropper_Click(object sender, RoutedEventArgs e)
        {
            ScanCanvas.IsEyedropperMode = !ScanCanvas.IsEyedropperMode;
            BtnEyedropper.Background = ScanCanvas.IsEyedropperMode ? (SolidColorBrush)FindResource("ButtonPressed") : (SolidColorBrush)FindResource("ControlLightGray");
            TxtStatus.Text = ScanCanvas.IsEyedropperMode ? "スポイトモード: 画面上の未露光部（オレンジマスク）をクリックしてください。" : "準備完了";
        }

        private void ScanCanvas_ColorPicked(byte r, byte g, byte b)
        {
            ScanCanvas.IsEyedropperMode = false;
            BtnEyedropper.Background = (SolidColorBrush)FindResource("ControlLightGray");
            ApplyBaseColor(r, g, b);
            TxtStatus.Text = $"スポイト取得ベース色: R={r}, G={g}, B={b}";
        }

        private void ApplyBaseColor(byte r, byte g, byte b)
        {
            if (_selectedFrame != null)
            {
                _selectedFrame.BaseColorR = r;
                _selectedFrame.BaseColorG = g;
                _selectedFrame.BaseColorB = b;
            }

            RectBaseColorSwatch.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
            TxtBaseColorRgb.Text = $"R: {r}  G: {g}  B: {b}";

            if (_selectedFrame != null)
            {
                UpdateFrameThumbnail(_selectedFrame);
                if (RbViewSingle.IsChecked == true) UpdateSingleFramePreview();
            }
        }

        private void ToneSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingUi || _selectedFrame == null) return;

            TxtExposureVal.Text = SldExposure.Value.ToString("F1");
            TxtContrastVal.Text = SldContrast.Value.ToString("F2");
            TxtSaturationVal.Text = SldSaturation.Value.ToString("F2");
            TxtColorTempVal.Text = SldColorTemp.Value.ToString("F0");
            TxtTintVal.Text = SldTint.Value.ToString("F0");

            _selectedFrame.Exposure = SldExposure.Value;
            _selectedFrame.Contrast = SldContrast.Value;
            _selectedFrame.Saturation = SldSaturation.Value;
            _selectedFrame.ColorTemp = SldColorTemp.Value;
            _selectedFrame.Tint = SldTint.Value;

            UpdateFrameThumbnail(_selectedFrame);
            if (RbViewSingle.IsChecked == true) UpdateSingleFramePreview();
        }

        private void BtnResetColor_Click(object sender, RoutedEventArgs e)
        {
            _isUpdatingUi = true;
            try
            {
                SldExposure.Value = 0.0;
                SldContrast.Value = 1.0;
                SldSaturation.Value = 1.0;
                SldColorTemp.Value = 0.0;
                SldTint.Value = 0.0;

                TxtExposureVal.Text = "0.0";
                TxtContrastVal.Text = "1.0";
                TxtSaturationVal.Text = "1.0";
                TxtColorTempVal.Text = "0";
                TxtTintVal.Text = "0";

                if (_selectedFrame != null)
                {
                    _selectedFrame.Exposure = 0.0;
                    _selectedFrame.Contrast = 1.0;
                    _selectedFrame.Saturation = 1.0;
                    _selectedFrame.ColorTemp = 0.0;
                    _selectedFrame.Tint = 0.0;
                    UpdateFrameThumbnail(_selectedFrame);
                    if (RbViewSingle.IsChecked == true) UpdateSingleFramePreview();
                }
            }
            finally
            {
                _isUpdatingUi = false;
            }
        }

        private void ChkIceEnable_Changed(object sender, RoutedEventArgs e)
        {
            if (_selectedFrame != null)
            {
                _selectedFrame.DustRemovalEnabled = ChkIceEnable.IsChecked == true;
            }
        }

        private void RbIceStrength_Checked(object sender, RoutedEventArgs e)
        {
            if (_selectedFrame == null) return;
            if (RbIceWeak.IsChecked == true) _selectedFrame.DustRemovalStrength = 1;
            else if (RbIceMedium.IsChecked == true) _selectedFrame.DustRemovalStrength = 2;
            else if (RbIceStrong.IsChecked == true) _selectedFrame.DustRemovalStrength = 3;
        }

        private void BtnRunIceInpaint_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedFrame == null || _currentScanMat == null || _currentScanMat.IsDisposed)
            {
                MessageBox.Show("コマを選択してください。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            TxtStatus.Text = $"コマ #{_selectedFrame.FrameNumber} の赤外線ゴミ除去処理を実行中...";
            UpdateFrameThumbnail(_selectedFrame);
            if (RbViewSingle.IsChecked == true) UpdateSingleFramePreview();
            TxtStatus.Text = "ゴミ除去処理が完了しました。";
        }

        // ======================================================================
        // コマ選択 & サムネイル更新
        // ======================================================================

        private void ScanCanvas_FrameSelected(FilmFrame frame)
        {
            SelectFrame(frame);
        }

        private void ScanCanvas_FrameModified(FilmFrame frame)
        {
            UpdateFrameThumbnail(frame);
            if (RbViewSingle.IsChecked == true) UpdateSingleFramePreview();
        }

        private void LstFilmStrip_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LstFilmStrip.SelectedItem is FilmFrame frame && frame != _selectedFrame)
            {
                SelectFrame(frame);
            }
        }

        private void SelectFrame(FilmFrame frame)
        {
            _isUpdatingUi = true;
            try
            {
                _selectedFrame = frame;

                foreach (var f in _currentRoll.AllFrames)
                {
                    f.IsSelected = (f == frame);
                }

                LstFilmStrip.SelectedItem = frame;
                DgFramesTable.SelectedItem = frame;
                ScanCanvas.SelectedFrame = frame;

                // コントロールへ値を反映
                RectBaseColorSwatch.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(frame.BaseColorR, frame.BaseColorG, frame.BaseColorB));
                TxtBaseColorRgb.Text = $"R: {frame.BaseColorR}  G: {frame.BaseColorG}  B: {frame.BaseColorB}";

                RbColor.IsChecked = frame.IsColor;
                RbBw.IsChecked = !frame.IsColor;
                RbNegative.IsChecked = frame.IsNegative;
                RbPositive.IsChecked = !frame.IsNegative;

                SldExposure.Value = frame.Exposure;
                SldContrast.Value = frame.Contrast;
                SldSaturation.Value = frame.Saturation;
                SldColorTemp.Value = frame.ColorTemp;
                SldTint.Value = frame.Tint;

                TxtExposureVal.Text = frame.Exposure.ToString("F1");
                TxtContrastVal.Text = frame.Contrast.ToString("F2");
                TxtSaturationVal.Text = frame.Saturation.ToString("F2");
                TxtColorTempVal.Text = frame.ColorTemp.ToString("F0");
                TxtTintVal.Text = frame.Tint.ToString("F0");

                ChkIceEnable.IsChecked = frame.DustRemovalEnabled;
                if (frame.DustRemovalStrength == 1) RbIceWeak.IsChecked = true;
                else if (frame.DustRemovalStrength == 3) RbIceStrong.IsChecked = true;
                else RbIceMedium.IsChecked = true;

                SyncFrameControlPanelInputs();

                if (RbViewSingle.IsChecked == true)
                {
                    UpdateSingleFramePreview();
                }
            }
            finally
            {
                _isUpdatingUi = false;
            }
        }

        private void UpdateFrameThumbnail(FilmFrame frame)
        {
            if (_currentScanMat == null || _currentScanMat.IsDisposed) return;
            try
            {
                var crop = frame.GetInsetCropRect();
                if (crop.Width <= 0 || crop.Height <= 0) return;

                int x = Math.Max(0, Math.Min(crop.X, _currentScanMat.Width - 1));
                int y = Math.Max(0, Math.Min(crop.Y, _currentScanMat.Height - 1));
                int w = Math.Min(crop.Width, _currentScanMat.Width - x);
                int h = Math.Min(crop.Height, _currentScanMat.Height - y);

                using var croppedMat = new Mat(_currentScanMat, new OpenCvSharp.Rect(x, y, w, h));
                using var invertedMat = _negativeEngine.ConvertNegativeToPositive(croppedMat, frame);
                using var rotatedMat = ApplyRotation(invertedMat, frame.RotationDegrees);

                // サムネイル用に小さくリサイズ
                int thumbW = 160;
                int thumbH = Math.Max(1, (int)(thumbW * ((double)rotatedMat.Height / rotatedMat.Width)));
                using var thumbMat = new Mat();
                Cv2.Resize(rotatedMat, thumbMat, new OpenCvSharp.Size(thumbW, thumbH));

                frame.Thumbnail = thumbMat.ToBitmapSource();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"UpdateFrameThumbnail error: {ex.Message}");
            }
        }

        private void UpdateFrameSummary()
        {
            int count = _currentRoll.AllFrames.Count;
            TxtFrameSummary.Text = $"{count} コマ登録";
            TxtFilmstripHeader.Text = $"フィルムストリップ (Film Strip - 全 {count} コマ)";
        }

        // ======================================================================
        // エクスポート (書き出し)
        // ======================================================================

        private async void BtnExportFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_currentRoll.AllFrames.Count == 0)
            {
                MessageBox.Show("エクスポートするコマがありません。[Pre-Scan] でコマを検出してください。", "案内", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new OpenFolderDialog
            {
                Title = "書き出し先フォルダを選択してください"
            };

            if (dialog.ShowDialog() == true)
            {
                string targetFolder = dialog.FolderName;
                SetScanningUiState(true);
                var progress = new Progress<(string message, double progress)>(p => TxtStatus.Text = p.message);

                try
                {
                    var options = new ExportOptions { OutputDirectory = targetFolder };
                    await _exportService.ExportToFolderAsync(_currentRoll, options, progress);
                    MessageBox.Show($"フォルダへの一括書き出しが完了しました:\n{targetFolder}", "書き出し成功", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"書き出し中にエラーが発生しました: {ex.Message}", "エクスポートエラー", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    SetScanningUiState(false);
                }
            }
        }

        private async void BtnExportZip_Click(object sender, RoutedEventArgs e)
        {
            if (_currentRoll.AllFrames.Count == 0)
            {
                MessageBox.Show("エクスポートするコマがありません。", "案内", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var sfd = new SaveFileDialog
            {
                Title = "ZIPアーカイブの保存先を指定してください",
                Filter = "ZIP Archive (*.zip)|*.zip",
                FileName = $"Roll_{DateTime.Now:yyyyMMdd_HHmmss}.zip"
            };

            if (sfd.ShowDialog() == true)
            {
                SetScanningUiState(true);
                var progress = new Progress<(string message, double progress)>(p => TxtStatus.Text = p.message);

                try
                {
                    var options = new ExportOptions { OutputZipPath = sfd.FileName };
                    await _exportService.ExportToZipAsync(_currentRoll, options, progress);
                    MessageBox.Show($"ZIPアーカイブへの書き出しが完了しました:\n{sfd.FileName}", "書き出し成功", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"ZIP書き出し中にエラーが発生しました: {ex.Message}", "エクスポートエラー", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    SetScanningUiState(false);
                }
            }
        }

        private async void BtnContactSheet_Click(object sender, RoutedEventArgs e)
        {
            if (_currentRoll.AllFrames.Count == 0)
            {
                MessageBox.Show("コマがありません。", "案内", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var sfd = new SaveFileDialog
            {
                Title = "コンタクトシートの保存先",
                Filter = "JPEG Image (*.jpg)|*.jpg|TIFF Image (*.tif)|*.tif",
                FileName = $"ContactSheet_{DateTime.Now:yyyyMMdd_HHmmss}.jpg"
            };

            if (sfd.ShowDialog() == true)
            {
                SetScanningUiState(true);
                try
                {
                    await Task.Run(() =>
                    {
                        using var csMat = _exportService.GenerateContactSheetMat(_currentRoll);
                        Cv2.ImWrite(sfd.FileName, csMat);
                    });
                    MessageBox.Show($"コンタクトシートを作成・保存しました:\n{sfd.FileName}", "完了", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"コンタクトシート作成エラー: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    SetScanningUiState(false);
                }
            }
        }
    }
}