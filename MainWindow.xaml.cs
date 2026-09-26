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
        private readonly ScanDurationTracker _durationTracker = new();

        private System.Windows.Threading.DispatcherTimer? _countdownTimer;
        private System.Diagnostics.Stopwatch? _scanStopwatch;
        private double _estimatedTotalSeconds = 0;
        private int _currentScanningDpi = 300;
        private int _currentScanningBitDepth = 8;

        private RollSession _currentRoll = new();
        private FilmStrip? _currentStrip = null;
        private FilmFrame? _selectedFrame = null;
        private ScannerDeviceInfo? _activeScanner = null;

        private Mat? _currentScanMat = null;
        private Mat? _currentIrMat = null;
        private string? _currentLoadedScanPath = null;
        private bool _isUpdatingUi = false;
        private readonly Dictionary<string, BitmapSource> _stripPreviewCache = new();

        public MainWindow()
        {
            InitializeComponent();

            _exportService = new RollExportService(_negativeEngine, _dustService, _exifService);

            InitializeFormatControls();
            InitializeProfileCombo();
            InitializeDpiCombo();
            InitializeBitDepthCombo();
            InitializeSession();

            ScanCanvas.FrameSelected += (s, frame) => SelectFrame(frame);
            ScanCanvas.StripSelected += (s, strip) =>
            {
                if (_currentStrip != strip)
                {
                    LstCuts.SelectedItem = strip;
                }
            };
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

            ScanCanvas.ZoomChanged += (s, scale) =>
            {
                if (TxtZoomLevel != null)
                {
                    TxtZoomLevel.Text = $"{(int)Math.Round(scale * 100)}%";
                }
            };

            ScanCanvas.AttachScrollBars(CanvasHScrollBar, CanvasVScrollBar, CanvasCornerBorder);

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
            CmbFilmSizeCategory.Items.Add("240 (APS)");
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

        private void InitializeBitDepthCombo()
        {
            CmbBitDepth.Items.Clear();
            CmbBitDepth.Items.Add("24-bit (8-bit/ch) [標準]");
            CmbBitDepth.Items.Add("48-bit (16-bit/ch) [高階調]");
            CmbBitDepth.SelectedIndex = 0; // デフォルト 24-bit
        }

        private int GetSelectedBitDepth()
        {
            if (CmbBitDepth.SelectedItem is string text && text.Contains("48-bit"))
            {
                return 16;
            }
            return 8;
        }

        private void CmbBitDepth_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingUi) return;
            int dpi = GetSelectedScanDpi();
            int bitDepth = GetSelectedBitDepth();
            double estSec = _durationTracker.GetEstimatedDurationSeconds(dpi, bitDepth);
            TxtStatus.Text = $"スキャン設定: {dpi} DPI / {(bitDepth >= 16 ? "48-bit Color (16-bit/ch)" : "24-bit Color")} (推定所要時間: 約 {(int)Math.Round(estSec)} 秒)";
        }

        private void InitializeSession()
        {
            _currentRoll = new RollSession();

            // デフォルトはすべて完全空白
            TxtRollName.Text = string.Empty;
            CmbFilmBrand.Text = string.Empty;
            CmbRollIso.Text = string.Empty;

            // 初期カット（Cut 1）を作成して追加
            var initialStrip = new FilmStrip
            {
                StripIndex = 1,
                Name = "Cut 1",
                Status = StripStatus.NotScanned
            };
            _currentRoll.Strips.Add(initialStrip);
            _currentStrip = initialStrip;

            LstCuts.ItemsSource = _currentRoll.Strips;
            LstCuts.SelectedItem = initialStrip;

            LstFilmStrip.ItemsSource = initialStrip.Frames;
            DgFramesTable.ItemsSource = _currentRoll.AllFrames;

            UpdateCutSummary();
            UpdateFrameSummary();
            UpdateMultiStripCanvas();
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
        // 【上側】Machine Control イベントハンドラ & スキャン計測・カウントダウン
        // ======================================================================

        private void StartScanTimer(int dpi, int bitDepth, bool isPreScan = false)
        {
            _currentScanningDpi = dpi;
            _currentScanningBitDepth = bitDepth;
            _estimatedTotalSeconds = _durationTracker.GetEstimatedDurationSeconds(dpi, bitDepth);
            _scanStopwatch = System.Diagnostics.Stopwatch.StartNew();

            if (_countdownTimer == null)
            {
                _countdownTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(1)
                };
                _countdownTimer.Tick += CountdownTimer_Tick;
            }

            TxtCountdown.Foreground = new SolidColorBrush(isPreScan ? Color.FromRgb(40, 167, 69) : Color.FromRgb(0, 120, 215));
            TxtCountdown.Visibility = Visibility.Visible;
            TxtCountdown.Text = $"{(isPreScan ? "[Pre-Scan] " : "[本Scan] ")}残り 約 {(int)Math.Ceiling(_estimatedTotalSeconds)} 秒";
            PrgScan.IsIndeterminate = false;
            PrgScan.Value = 0;
            _countdownTimer.Start();
        }

        private void CountdownTimer_Tick(object? sender, EventArgs e)
        {
            if (_scanStopwatch == null) return;
            double elapsed = _scanStopwatch.Elapsed.TotalSeconds;
            double remaining = _estimatedTotalSeconds - elapsed;

            if (remaining > 0)
            {
                int remSec = (int)Math.Ceiling(remaining);
                double pct = Math.Min(95.0, (elapsed / _estimatedTotalSeconds) * 100.0);
                TxtCountdown.Text = $"残り 約 {remSec} 秒 ({pct:F0}%)";
                PrgScan.IsIndeterminate = false;
                PrgScan.Value = pct;
            }
            else
            {
                TxtCountdown.Text = "データ転送・画像生成中... (まもなく完了)";
                PrgScan.IsIndeterminate = true;
            }
        }

        private void StopScanTimer(bool success)
        {
            _countdownTimer?.Stop();
            TxtCountdown.Visibility = Visibility.Collapsed;

            if (_scanStopwatch != null)
            {
                _scanStopwatch.Stop();
                double actualSec = _scanStopwatch.Elapsed.TotalSeconds;
                if (success)
                {
                    _durationTracker.RecordActualDuration(_currentScanningDpi, _currentScanningBitDepth, actualSec);
                    TxtStatus.Text = $"スキャン完了！ (所要時間: {actualSec:F1}秒)";
                }
            }
        }

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
            int dpi = GetSelectedScanDpi();
            int bitDepth = GetSelectedBitDepth();
            SetScanningUiState(true);
            StartScanTimer(dpi, bitDepth);
            var progress = new Progress<string>(msg => TxtStatus.Text = msg);
            bool success = false;

            try
            {
                var (colorMat, irMat) = await _scannerService.ScanWithDialogAsync(bitDepth, progress);
                if (_currentStrip == null)
                {
                    int nextIdx = _currentRoll.Strips.Count + 1;
                    _currentStrip = new FilmStrip { StripIndex = nextIdx, Name = $"Cut {nextIdx}", Status = StripStatus.NotScanned };
                    _currentRoll.Strips.Add(_currentStrip);
                    LstCuts.SelectedItem = _currentStrip;
                }
                ApplyScanDataToCut(_currentStrip, colorMat, irMat, dpi, isPreScan: false);
                success = true;
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
                StopScanTimer(success);
                SetScanningUiState(false);
            }
        }

        private async Task RunScanAsync(int dpi, bool isPreScan)
        {
            int bitDepth = isPreScan ? 8 : GetSelectedBitDepth();
            SetScanningUiState(true);
            StartScanTimer(dpi, bitDepth, isPreScan);
            var progress = new Progress<string>(msg => TxtStatus.Text = msg);
            bool success = false;

            try
            {
                // 選択中のカットがなければ作成
                if (_currentStrip == null)
                {
                    int nextIdx = _currentRoll.Strips.Count + 1;
                    _currentStrip = new FilmStrip
                    {
                        StripIndex = nextIdx,
                        Name = $"Cut {nextIdx}",
                        Status = StripStatus.NotScanned
                    };
                    _currentRoll.Strips.Add(_currentStrip);
                    LstCuts.SelectedItem = _currentStrip;
                }

                // フィルムスキャンのため isTransmissive = true (TPU 透過光ユニット点灯)
                var (colorMat, irMat) = await _scannerService.ScanAsync(_activeScanner, dpi, bitDepth, true, null, progress);

                ApplyScanDataToCut(_currentStrip, colorMat, irMat, dpi, isPreScan);
                success = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"スキャン中にエラーが発生しました: {ex.Message}", "スキャンエラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                StopScanTimer(success);
                SetScanningUiState(false);
            }
        }

        private void SetScanningUiState(bool isScanning)
        {
            BtnPreScan.IsEnabled = !isScanning;
            BtnScan.IsEnabled = !isScanning;
            BtnScannerSetting.IsEnabled = !isScanning;
            PrgScan.Visibility = isScanning ? Visibility.Visible : Visibility.Collapsed;
            if (!isScanning)
            {
                PrgScan.IsIndeterminate = false;
                PrgScan.Value = 0;
            }
            SetScannerStatus(isScanning ? ScannerState.Busy : ScannerState.Ready, isScanning ? "Scanning..." : "Ready");
        }

        private void UpdateScanCanvasImage(Mat? mat)
        {
            if (mat == null || mat.IsDisposed)
            {
                ScanCanvas.ImageSource = null;
                ScanCanvas.LogicalImageWidth = 0;
                ScanCanvas.LogicalImageHeight = 0;
                return;
            }

            ScanCanvas.LogicalImageWidth = mat.Width;
            ScanCanvas.LogicalImageHeight = mat.Height;

            // 16-bit画像の場合は表示用に8-bitへ安全変換
            Mat effectiveMat = mat;
            bool disposeEffective = false;
            if (mat.Depth() == MatType.CV_16U)
            {
                effectiveMat = new Mat();
                mat.ConvertTo(effectiveMat, MatType.CV_8UC3, 1.0 / 257.0);
                disposeEffective = true;
            }

            try
            {
                // 長辺が3000pxを超える高解像度画像の場合、UI描画用の軽量プレビュー（長辺2860px）を生成してWIC/RAMを劇的削減（800MB -> ~6MB）
                int maxDim = Math.Max(effectiveMat.Width, effectiveMat.Height);
                if (maxDim > 3000)
                {
                    double scale = 2860.0 / maxDim;
                    int previewW = (int)Math.Round(effectiveMat.Width * scale);
                    int previewH = (int)Math.Round(effectiveMat.Height * scale);
                    using var previewMat = new Mat();
                    Cv2.Resize(effectiveMat, previewMat, new OpenCvSharp.Size(previewW, previewH), 0, 0, InterpolationFlags.Area);
                    ScanCanvas.ImageSource = previewMat.ToBitmapSource();
                }
                else
                {
                    ScanCanvas.ImageSource = effectiveMat.ToBitmapSource();
                }
            }
            finally
            {
                if (disposeEffective)
                {
                    effectiveMat.Dispose();
                }
            }
        }

        private BitmapSource? GetStripPreviewImage(FilmStrip strip)
        {
            if (strip == _currentStrip && ScanCanvas.ImageSource != null)
            {
                return ScanCanvas.ImageSource;
            }

            if (_stripPreviewCache.TryGetValue(strip.Id, out var cached))
            {
                return cached;
            }

            if (!string.IsNullOrEmpty(strip.FullScanImagePath) && System.IO.File.Exists(strip.FullScanImagePath))
            {
                try
                {
                    using var mat = Cv2.ImRead(strip.FullScanImagePath, ImreadModes.Color);
                    if (!mat.Empty())
                    {
                        int maxDim = Math.Max(mat.Width, mat.Height);
                        if (maxDim > 2000)
                        {
                            double sc = 2000.0 / maxDim;
                            int nw = (int)Math.Round(mat.Width * sc);
                            int nh = (int)Math.Round(mat.Height * sc);
                            using var previewMat = new Mat();
                            Cv2.Resize(mat, previewMat, new OpenCvSharp.Size(nw, nh), 0, 0, InterpolationFlags.Area);
                            var bmp = previewMat.ToBitmapSource();
                            _stripPreviewCache[strip.Id] = bmp;
                            return bmp;
                        }
                        else
                        {
                            var bmp = mat.ToBitmapSource();
                            _stripPreviewCache[strip.Id] = bmp;
                            return bmp;
                        }
                    }
                }
                catch
                {
                    // 読み込み失敗時はnull
                }
            }

            return null;
        }

        private void UpdateMultiStripCanvas()
        {
            if (_currentRoll == null || _currentRoll.Strips.Count == 0)
            {
                ScanCanvas.SetMultiStrips(null, null, _ => null);
                return;
            }

            ScanCanvas.SetMultiStrips(_currentRoll.Strips, _currentStrip, GetStripPreviewImage);
        }

        private void ApplyScanDataToCut(FilmStrip strip, Mat colorMat, Mat? irMat, int dpi, bool isPreScan)
        {
            _isUpdatingUi = true;
            try
            {
                if (_currentScanMat != null && !_currentScanMat.IsDisposed)
                {
                    _currentScanMat.Dispose();
                    _currentScanMat = null;
                }
                if (_currentIrMat != null && !_currentIrMat.IsDisposed)
                {
                    _currentIrMat.Dispose();
                    _currentIrMat = null;
                }

                // クローンせず直接所有権を引き継いでRAM消費を半減
                _currentScanMat = colorMat;
                _currentIrMat = irMat;

                // 本スキャンの場合、PreScanで検知された傾き角度を自動適用して正立補正
                // もし未検知（0.0°）の場合は、本スキャン画像から直接高精度に傾きを再検知
                double targetSkew = strip.SkewAngle;
                if (!isPreScan && Math.Abs(targetSkew) < 0.1)
                {
                    targetSkew = _detectorService.DetectFilmSkewAngleFromMediaBoundary(_currentScanMat);
                    if (Math.Abs(targetSkew) >= 0.1)
                    {
                        strip.SkewAngle = targetSkew;
                    }
                }

                if (!isPreScan && Math.Abs(targetSkew) >= 0.1)
                {
                    var straight = _detectorService.StraightenImage(_currentScanMat, targetSkew);
                    _currentScanMat.Dispose();
                    _currentScanMat = straight;

                    if (_currentIrMat != null && !_currentIrMat.IsDisposed)
                    {
                        var straightIr = _detectorService.StraightenImage(_currentIrMat, targetSkew);
                        _currentIrMat.Dispose();
                        _currentIrMat = straightIr;
                    }
                }

                var (rawPath, irPath) = _sessionService.SaveStripImages(_currentRoll.SessionId, strip.Id, _currentScanMat, _currentIrMat);
                strip.FullScanImagePath = rawPath;
                strip.FullScanIrPath = irPath;
                _currentLoadedScanPath = rawPath;
                strip.ScanDpi = dpi;
                strip.ScannedAt = DateTime.Now;

                // UIプレビュー表示（ダウンサンプリング適用）
                UpdateScanCanvasImage(_currentScanMat);

                if (isPreScan)
                {
                    // Pre-Scan: 自動コマ認識を実行してコマ枠を検出
                    strip.Status = StripStatus.PreScanned;
                    PerformAutoDetectFramesOnStrip(strip, _currentScanMat, _currentIrMat);
                    strip.PreScanWidth = _currentScanMat.Width;
                    strip.PreScanHeight = _currentScanMat.Height;
                    foreach (var f in strip.Frames) f.Status = StripStatus.PreScanned;
                    TxtStatus.Text = $"{strip.Name}: Pre-Scan完了 ({strip.Frames.Count}コマ検出)。コマ枠を確認・微調整して [Scan] を実行してください。";
                }
                else
                {
                    // 本スキャン:
                    // 既存のコマ枠（PreScanや微調整結果）がある場合、絶対に初期化せず解像度比率で拡大スケーリングして完全保持
                    if (strip.Frames.Count > 0)
                    {
                        int sourceDpi = strip.FrameCoordinatesDpi > 0 ? strip.FrameCoordinatesDpi : 300;
                        double scaleX = (double)dpi / sourceDpi;
                        double scaleY = (double)dpi / sourceDpi;

                        // PreScan実寸画像サイズが記録されている場合は実寸ピクセル比率で精密スケーリング
                        if (strip.PreScanWidth > 0 && strip.PreScanHeight > 0)
                        {
                            scaleX = (double)_currentScanMat.Width / strip.PreScanWidth;
                            scaleY = (double)_currentScanMat.Height / strip.PreScanHeight;
                        }

                        foreach (var f in strip.Frames)
                        {
                            var r = f.CropRect;
                            int sx = (int)Math.Round(r.X * scaleX);
                            int sy = (int)Math.Round(r.Y * scaleY);
                            int sw = (int)Math.Round(r.Width * scaleX);
                            int sh = (int)Math.Round(r.Height * scaleY);

                            sx = Math.Max(0, Math.Min(sx, _currentScanMat.Width - 10));
                            sy = Math.Max(0, Math.Min(sy, _currentScanMat.Height - 10));
                            sw = Math.Min(sw, _currentScanMat.Width - sx);
                            sh = Math.Min(sh, _currentScanMat.Height - sy);

                            f.CropRect = new OpenCvSharp.Rect(sx, sy, sw, sh);
                            f.RawImagePath = strip.FullScanImagePath;
                            f.IrImagePath = strip.FullScanIrPath;
                            f.Status = StripStatus.Scanned;
                            UpdateFrameThumbnail(f);
                        }
                        strip.FrameCoordinatesDpi = dpi;
                    }
                    else
                    {
                        // コマ枠が未登録の場合のみ自動認識を実行
                        PerformAutoDetectFramesOnStrip(strip, _currentScanMat, _currentIrMat);
                        foreach (var f in strip.Frames) f.Status = StripStatus.Scanned;
                    }

                    strip.Status = StripStatus.Scanned;
                    TxtStatus.Text = $"{strip.Name}: 本スキャン完了 ({strip.Frames.Count}コマ)。続いて [＋ 次のカット] をセットするか、[ロール一括書き出し] を実行してください。";
                }

                _stripPreviewCache.Remove(strip.Id);
                SyncAllFramesFromStrips();
                SelectCut(strip);
                UpdateMultiStripCanvas();

                // ガベージコレクションを強制実行して中間バッファ・DirectXメモリを即時回収
                GC.Collect(2, GCCollectionMode.Forced, true);
                GC.WaitForPendingFinalizers();
            }
            finally
            {
                _isUpdatingUi = false;
            }
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

        private void NavigateFrameRelative(int delta)
        {
            if (_currentRoll.AllFrames.Count == 0) return;
            if (_selectedFrame == null)
            {
                SelectFrame(_currentRoll.AllFrames[0]);
                return;
            }

            int idx = _currentRoll.AllFrames.IndexOf(_selectedFrame);
            int newIdx = Math.Clamp(idx + delta, 0, _currentRoll.AllFrames.Count - 1);
            if (newIdx != idx)
            {
                SelectFrame(_currentRoll.AllFrames[newIdx]);
                LstFilmStrip.ScrollIntoView(_currentRoll.AllFrames[newIdx]);
            }
        }

        private void BtnPrevFrame_Click(object sender, RoutedEventArgs e) => NavigateFrameRelative(-1);
        private void BtnNextFrame_Click(object sender, RoutedEventArgs e) => NavigateFrameRelative(1);

        private void BtnZoomIn_Click(object sender, RoutedEventArgs e) => ScanCanvas.ZoomIn();
        private void BtnZoomOut_Click(object sender, RoutedEventArgs e) => ScanCanvas.ZoomOut();
        private void BtnZoom100_Click(object sender, RoutedEventArgs e) => ScanCanvas.ZoomActualSize();
        private void BtnZoomFit_Click(object sender, RoutedEventArgs e) => ScanCanvas.ResetView();

        private void BtnBackToFullView_Click(object sender, RoutedEventArgs e)
        {
            RbViewFull.IsChecked = true;
            ViewMode_Changed(sender, e);
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

        private void BtnShortcutsHelp_Click(object sender, RoutedEventArgs e)
        {
            string helpText =
                "【IRIS PxS キーボードショートカット一覧】\n\n" +
                "◆ コマ移動・選択\n" +
                "  ・ PageUp / [ : 前のコマを選択\n" +
                "  ・ PageDown / ] : 次のコマを選択\n" +
                "  ・ ↑ / ↓ キー : 全コマを一括ナッジ微調整 (Shift併用で10px移動)\n\n" +
                "◆ 表示・拡大縮小\n" +
                "  ・ + / - : 表示拡大 / 縮小\n" +
                "  ・ 0 (ゼロ) : 等倍表示 (100%)\n" +
                "  ・ F キー : 画面全体に合わせる (Fit)\n" +
                "  ・ マウスホイール : 拡大縮小 (カーソル位置中心)\n\n" +
                "◆ ビュー切替\n" +
                "  ・ Ctrl + 1 : スキャン全体ビュー\n" +
                "  ・ Ctrl + 2 : コマ個別確認ビュー\n" +
                "  ・ Ctrl + 3 : 全コマメタデータ設定表\n" +
                "  ・ Ctrl + 4 : ICE 赤外線ゴミ・キズ差分マップ\n\n" +
                "◆ 編集・書き出し\n" +
                "  ・ R キー : コマを90度右回転\n" +
                "  ・ Ctrl + T : 自動トーン補正 (露出/コントラスト/WB最適化)\n" +
                "  ・ Delete : 選択中のコマ枠を削除\n" +
                "  ・ Ctrl + E : フォルダへ一括書き出し\n" +
                "  ・ フィルムストリップ ダブルクリック : 個別ビューで拡大確認";

            MessageBox.Show(helpText, "キーボードショートカット早見表", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.OriginalSource is TextBox)
            {
                if (e.Key == Key.Escape)
                {
                    Keyboard.ClearFocus();
                    e.Handled = true;
                }
                return;
            }

            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                switch (e.Key)
                {
                    case Key.T:
                        BtnAutoTone_Click(sender, e);
                        e.Handled = true;
                        break;
                    case Key.E:
                        BtnExportFolder_Click(sender, e);
                        e.Handled = true;
                        break;
                    case Key.D1:
                        RbViewFull.IsChecked = true;
                        ViewMode_Changed(sender, e);
                        e.Handled = true;
                        break;
                    case Key.D2:
                        RbViewSingle.IsChecked = true;
                        ViewMode_Changed(sender, e);
                        e.Handled = true;
                        break;
                    case Key.D3:
                        RbViewTable.IsChecked = true;
                        ViewMode_Changed(sender, e);
                        e.Handled = true;
                        break;
                    case Key.D4:
                        RbViewIce.IsChecked = true;
                        ViewMode_Changed(sender, e);
                        e.Handled = true;
                        break;
                }
            }
            else
            {
                switch (e.Key)
                {
                    case Key.PageUp:
                    case Key.OemOpenBrackets:
                        NavigateFrameRelative(-1);
                        e.Handled = true;
                        break;
                    case Key.PageDown:
                    case Key.OemCloseBrackets:
                        NavigateFrameRelative(1);
                        e.Handled = true;
                        break;
                    case Key.Up:
                        NudgeFrames(isForward: false);
                        e.Handled = true;
                        break;
                    case Key.Down:
                        NudgeFrames(isForward: true);
                        e.Handled = true;
                        break;
                    case Key.R:
                        BtnRotateFrame_Click(sender, e);
                        e.Handled = true;
                        break;
                    case Key.F:
                        BtnZoomFit_Click(sender, e);
                        e.Handled = true;
                        break;
                    case Key.Delete:
                        BtnDeleteFrame_Click(sender, e);
                        e.Handled = true;
                        break;
                    case Key.F1:
                        BtnShortcutsHelp_Click(sender, e);
                        e.Handled = true;
                        break;
                    case Key.Add:
                    case Key.OemPlus:
                        ScanCanvas.ZoomIn();
                        e.Handled = true;
                        break;
                    case Key.Subtract:
                    case Key.OemMinus:
                        ScanCanvas.ZoomOut();
                        e.Handled = true;
                        break;
                    case Key.D0:
                    case Key.NumPad0:
                        ScanCanvas.ZoomActualSize();
                        e.Handled = true;
                        break;
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
            if (_isUpdatingUi) return;
            int dpi = GetSelectedScanDpi();
            int bitDepth = GetSelectedBitDepth();
            double estSec = _durationTracker.GetEstimatedDurationSeconds(dpi, bitDepth);
            TxtStatus.Text = $"スキャン設定: {dpi} DPI / {(bitDepth >= 16 ? "48-bit Color (16-bit/ch)" : "24-bit Color")} (推定所要時間: 約 {(int)Math.Round(estSec)} 秒)";
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
            string? brand = CmbFilmBrand.SelectedItem as string ?? CmbFilmBrand.Text;
            if (string.IsNullOrWhiteSpace(brand)) return;
            _currentRoll.FilmStock = brand;

            // 銘柄プリセットからモノクロ/カラー及びISOを自動判定して反映
            bool isBw = brand.Contains("Tri-X", StringComparison.OrdinalIgnoreCase) ||
                        brand.Contains("Acros", StringComparison.OrdinalIgnoreCase) ||
                        brand.Contains("HP5", StringComparison.OrdinalIgnoreCase) ||
                        brand.Contains("T-Max", StringComparison.OrdinalIgnoreCase) ||
                        brand.Contains("Delta", StringComparison.OrdinalIgnoreCase);

            RbBw.IsChecked = isBw;
            RbColor.IsChecked = !isBw;

            if (brand.Contains("100")) CmbRollIso.Text = "100";
            else if (brand.Contains("160")) CmbRollIso.Text = "160";
            else if (brand.Contains("200")) CmbRollIso.Text = "200";
            else if (brand.Contains("400")) CmbRollIso.Text = "400";
            else if (brand.Contains("800")) CmbRollIso.Text = "800";

            FilmType_Changed(sender, e);
        }

        private void CmbRollIso_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbRollIso.SelectedItem is string isoStr && int.TryParse(isoStr, out int iso))
            {
                _currentRoll.DefaultIso = iso;
            }
        }

        private void LstCuts_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingUi) return;
            if (LstCuts.SelectedItem is FilmStrip selectedStrip)
            {
                SelectCut(selectedStrip);
            }
        }

        private void SelectCut(FilmStrip strip)
        {
            if (strip == null) return;
            _currentStrip = strip;

            // スキャン画像が存在すればロードして ScanCanvas に表示
            if (!string.IsNullOrEmpty(strip.FullScanImagePath) && File.Exists(strip.FullScanImagePath))
            {
                if (_currentScanMat == null || _currentScanMat.IsDisposed || _currentLoadedScanPath != strip.FullScanImagePath)
                {
                    if (_currentScanMat != null && !_currentScanMat.IsDisposed) _currentScanMat.Dispose();
                    _currentScanMat = Cv2.ImRead(strip.FullScanImagePath);
                    _currentLoadedScanPath = strip.FullScanImagePath;

                    if (!string.IsNullOrEmpty(strip.FullScanIrPath) && File.Exists(strip.FullScanIrPath))
                    {
                        if (_currentIrMat != null && !_currentIrMat.IsDisposed) _currentIrMat.Dispose();
                        _currentIrMat = Cv2.ImRead(strip.FullScanIrPath);
                    }
                    else
                    {
                        if (_currentIrMat != null && !_currentIrMat.IsDisposed) _currentIrMat.Dispose();
                        _currentIrMat = null;
                    }
                }

                UpdateScanCanvasImage(_currentScanMat);
            }
            else
            {
                if (_currentScanMat != null && !_currentScanMat.IsDisposed) _currentScanMat.Dispose();
                _currentScanMat = null;
                if (_currentIrMat != null && !_currentIrMat.IsDisposed) _currentIrMat.Dispose();
                _currentIrMat = null;
                _currentLoadedScanPath = null;
                UpdateScanCanvasImage(null);
            }

            // コマ枠とサムネイルバーを選択中カットのものに切り替え
            ScanCanvas.Frames = strip.Frames;
            ScanCanvas.InvalidateVisual();

            LstFilmStrip.ItemsSource = null;
            LstFilmStrip.ItemsSource = strip.Frames;
            TxtFilmstripHeader.Text = $"フィルムストリップ ({strip.Name} - {strip.StatusText})";

            if (strip.Frames.Count > 0)
            {
                if (_selectedFrame != null && strip.Frames.Contains(_selectedFrame))
                {
                    SelectFrame(_selectedFrame);
                }
                else
                {
                    SelectFrame(strip.Frames[0]);
                }
            }
            else
            {
                _selectedFrame = null;
                ScanCanvas.SelectedFrame = null;
                ScanCanvas.InvalidateVisual();
            }

            UpdateFrameSummary();
            UpdateMultiStripCanvas();
        }

        private void SyncAllFramesFromStrips()
        {
            _currentRoll.AllFrames.Clear();
            int frameNum = 1;
            foreach (var strip in _currentRoll.Strips)
            {
                foreach (var frame in strip.Frames)
                {
                    frame.FrameNumber = frameNum++;
                    _currentRoll.AllFrames.Add(frame);
                }
                strip.NotifyFrameCountChanged();
            }
            DgFramesTable.ItemsSource = null;
            DgFramesTable.ItemsSource = _currentRoll.AllFrames;
            UpdateCutSummary();
            UpdateFrameSummary();
        }

        private void UpdateCutSummary()
        {
            if (TxtCutSummary != null)
            {
                int totalCuts = _currentRoll.Strips.Count;
                int scannedCuts = _currentRoll.Strips.Count(s => s.Status == StripStatus.Scanned);
                TxtCutSummary.Text = $"{totalCuts} カット (完了: {scannedCuts})";
            }
        }

        private void BtnNewCut_Click(object sender, RoutedEventArgs e)
        {
            int nextIndex = _currentRoll.Strips.Count + 1;
            var newStrip = new FilmStrip
            {
                StripIndex = nextIndex,
                Name = $"Cut {nextIndex}",
                Status = StripStatus.NotScanned
            };
            _currentRoll.Strips.Add(newStrip);
            UpdateCutSummary();
            LstCuts.SelectedItem = newStrip;
            SelectCut(newStrip);
            UpdateMultiStripCanvas();
            TxtStatus.Text = $"{newStrip.Name} を追加しました。フィルムをセットして [Pre-Scan] を実行してください。";
        }

        private void BtnDeleteCut_Click(object sender, RoutedEventArgs e)
        {
            if (LstCuts.SelectedItem is not FilmStrip targetStrip) return;
            if (_currentRoll.Strips.Count <= 1)
            {
                MessageBox.Show("最低1つのカットが必要です。", "案内", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var res = MessageBox.Show($"{targetStrip.Name} を削除しますか？\n（含まれる {targetStrip.Frames.Count} コマも削除されます）", "確認", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res != MessageBoxResult.Yes) return;

            _currentRoll.Strips.Remove(targetStrip);

            for (int i = 0; i < _currentRoll.Strips.Count; i++)
            {
                _currentRoll.Strips[i].StripIndex = i + 1;
            }

            SyncAllFramesFromStrips();
            var fallback = _currentRoll.Strips.Last();
            LstCuts.SelectedItem = fallback;
            SelectCut(fallback);
            UpdateMultiStripCanvas();
        }

        private void BtnAutoDetectFrames_Click(object sender, RoutedEventArgs e)
        {
            if (_currentStrip == null || _currentScanMat == null || _currentScanMat.IsDisposed)
            {
                MessageBox.Show("スキャン画像または読み込み画像がありません。[Pre-Scan] を実行してください。", "案内", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            PerformAutoDetectFrames();
        }

        private void PerformAutoDetectFrames()
        {
            if (_currentStrip == null || _currentScanMat == null || _currentScanMat.IsDisposed) return;
            PerformAutoDetectFramesOnStrip(_currentStrip, _currentScanMat, _currentIrMat);
            SyncAllFramesFromStrips();
            SelectCut(_currentStrip);
        }

        private void PerformAutoDetectFramesOnStrip(FilmStrip strip, Mat scanMat, Mat? irMat)
        {
            var format = GetSelectedFormat();
            int dpi = strip.ScanDpi > 0 ? strip.ScanDpi : 300;
            TxtStatus.Text = $"フィルム全体のコントラストから傾き検知およびコマ枠配置を実行中 (フォーマット: {format.DisplayName}, {dpi} DPI)...";

            var (straightenedMat, skewAngle, detectedRects) = _detectorService.DetectAndStraighten(scanMat, format, dpi);

            if (Math.Abs(skewAngle) >= 0.1)
            {
                strip.SkewAngle = skewAngle;
            }
            strip.FrameCoordinatesDpi = dpi;

            // 傾きが検知された場合 (0.1度以上)、画像を正立（回転補正）した画像に差し替え
            if (Math.Abs(skewAngle) >= 0.1)
            {
                scanMat.Dispose();
                _currentScanMat = straightenedMat;

                if (irMat != null && !irMat.IsDisposed)
                {
                    var straightIr = _detectorService.StraightenImage(irMat, skewAngle);
                    irMat.Dispose();
                    _currentIrMat = straightIr;
                }

                var (rawPath, irPath) = _sessionService.SaveStripImages(_currentRoll.SessionId, strip.Id, _currentScanMat, _currentIrMat);
                strip.FullScanImagePath = rawPath;
                strip.FullScanIrPath = irPath;
                _currentLoadedScanPath = rawPath;

                UpdateScanCanvasImage(_currentScanMat);
            }
            else
            {
                straightenedMat.Dispose();
            }

            strip.PreScanWidth = _currentScanMat?.Width ?? scanMat.Width;
            strip.PreScanHeight = _currentScanMat?.Height ?? scanMat.Height;

            strip.Frames.Clear();

            int startNumber = 1;
            foreach (var r in detectedRects)
            {
                var frame = new FilmFrame
                {
                    FrameNumber = startNumber++,
                    StripId = strip.Id,
                    CropRect = r,
                    RawImagePath = strip.FullScanImagePath,
                    IrImagePath = strip.FullScanIrPath,
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

                strip.Frames.Add(frame);
                UpdateFrameThumbnail(frame);
            }

            TxtStatus.Text = Math.Abs(skewAngle) >= 0.1
                ? $"{strip.Name}: 傾き {skewAngle:F1}° を検知・自動正立補正し、{detectedRects.Count} コマを自動生成しました。"
                : $"{strip.Name}: {detectedRects.Count} コマを自動配置しました。";
        }

        private void BtnAddFrame_Click(object sender, RoutedEventArgs e)
        {
            if (_currentStrip == null || _currentScanMat == null || _currentScanMat.IsDisposed)
            {
                MessageBox.Show("スキャン画像がありません。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
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
            UpdateFrameThumbnail(newFrame);
            SyncAllFramesFromStrips();
            SelectCut(_currentStrip);
            SelectFrame(newFrame);
        }

        private void BtnDeleteFrame_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedFrame == null || _currentStrip == null)
            {
                MessageBox.Show("削除するコマ枠が選択されていません。", "案内", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _currentStrip.Frames.Remove(_selectedFrame);
            SyncAllFramesFromStrips();
            SelectCut(_currentStrip);
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

        private void BtnFrameCenter_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedFrame == null) return;
            ScanCanvas.CenterSelectedFrame();
            UpdateFrameThumbnail(_selectedFrame);
            SyncFrameControlPanelInputs();
            if (RbViewSingle?.IsChecked == true) UpdateSingleFramePreview();
            TxtStatus.Text = $"コマ #{_selectedFrame.FrameNumber} を中央に配置しました。";
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

        private void BtnAutoTone_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedFrame == null || _currentScanMat == null || _currentScanMat.IsDisposed)
            {
                MessageBox.Show("コマ枠を選択してください。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var (exposure, contrast, saturation, colorTemp, tint) = _negativeEngine.CalculateAutoTone(_currentScanMat, _selectedFrame);

            _isUpdatingUi = true;
            try
            {
                SldExposure.Value = exposure;
                SldContrast.Value = contrast;
                SldSaturation.Value = saturation;
                SldColorTemp.Value = colorTemp;
                SldTint.Value = tint;

                TxtExposureVal.Text = exposure.ToString("F1");
                TxtContrastVal.Text = contrast.ToString("F2");
                TxtSaturationVal.Text = saturation.ToString("F2");
                TxtColorTempVal.Text = colorTemp.ToString("F0");
                TxtTintVal.Text = tint.ToString("F0");

                _selectedFrame.Exposure = exposure;
                _selectedFrame.Contrast = contrast;
                _selectedFrame.Saturation = saturation;
                _selectedFrame.ColorTemp = colorTemp;
                _selectedFrame.Tint = tint;

                UpdateFrameThumbnail(_selectedFrame);
                if (RbViewSingle.IsChecked == true) UpdateSingleFramePreview();

                TxtStatus.Text = $"コマ #{_selectedFrame.FrameNumber} のトーンを自動最適化しました (EV:{exposure:F1}, コントラスト:{contrast:F2}, 色温度:{colorTemp:F0})";
            }
            finally
            {
                _isUpdatingUi = false;
            }
        }

        private void BtnApplyToneToAll_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedFrame == null || _currentRoll.AllFrames.Count == 0)
            {
                MessageBox.Show("コピー元のコマを選択してください。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var res = MessageBox.Show(
                $"コマ #{_selectedFrame.FrameNumber} のトーン設定（露出: {_selectedFrame.Exposure:F1}, コントラスト: {_selectedFrame.Contrast:F2}, 彩度: {_selectedFrame.Saturation:F2}, 色温度: {_selectedFrame.ColorTemp:F0}, 色合い: {_selectedFrame.Tint:F0}, ベース色）を全コマにコピーしますか？",
                "トーン設定の一括適用", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (res == MessageBoxResult.Yes)
            {
                foreach (var f in _currentRoll.AllFrames)
                {
                    f.Exposure = _selectedFrame.Exposure;
                    f.Contrast = _selectedFrame.Contrast;
                    f.Saturation = _selectedFrame.Saturation;
                    f.ColorTemp = _selectedFrame.ColorTemp;
                    f.Tint = _selectedFrame.Tint;
                    f.BaseColorR = _selectedFrame.BaseColorR;
                    f.BaseColorG = _selectedFrame.BaseColorG;
                    f.BaseColorB = _selectedFrame.BaseColorB;
                    UpdateFrameThumbnail(f);
                }

                if (RbViewSingle.IsChecked == true) UpdateSingleFramePreview();
                TxtStatus.Text = "全コマにトーン設定を一括コピーしました。";
            }
        }

        private void BtnPresetNeutral_Click(object sender, RoutedEventArgs e) => ApplyTonePreset(0.0, 1.0, 1.0, 0.0, 0.0, "標準 / Neutral");
        private void BtnPresetVivid_Click(object sender, RoutedEventArgs e) => ApplyTonePreset(0.1, 1.15, 1.25, 2.0, 0.0, "鮮やか / Vivid");
        private void BtnPresetCinema_Click(object sender, RoutedEventArgs e) => ApplyTonePreset(-0.1, 1.2, 0.9, -4.0, 4.0, "シネマ / Cinema");
        private void BtnPresetHighKey_Click(object sender, RoutedEventArgs e) => ApplyTonePreset(0.4, 0.95, 1.05, 3.0, 2.0, "ハイキー / HighKey");

        private void ApplyTonePreset(double exp, double con, double sat, double temp, double tint, string presetName)
        {
            if (_selectedFrame == null) return;
            _isUpdatingUi = true;
            try
            {
                SldExposure.Value = exp;
                SldContrast.Value = con;
                SldSaturation.Value = sat;
                SldColorTemp.Value = temp;
                SldTint.Value = tint;

                TxtExposureVal.Text = exp.ToString("F1");
                TxtContrastVal.Text = con.ToString("F2");
                TxtSaturationVal.Text = sat.ToString("F2");
                TxtColorTempVal.Text = temp.ToString("F0");
                TxtTintVal.Text = tint.ToString("F0");

                _selectedFrame.Exposure = exp;
                _selectedFrame.Contrast = con;
                _selectedFrame.Saturation = sat;
                _selectedFrame.ColorTemp = temp;
                _selectedFrame.Tint = tint;

                UpdateFrameThumbnail(_selectedFrame);
                if (RbViewSingle.IsChecked == true) UpdateSingleFramePreview();
                TxtStatus.Text = $"プリセット「{presetName}」を適用しました。";
            }
            finally
            {
                _isUpdatingUi = false;
            }
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

        private void LstFilmStrip_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (VisualTreeHelper.GetChildrenCount(LstFilmStrip) > 0)
            {
                var border = VisualTreeHelper.GetChild(LstFilmStrip, 0) as Decorator;
                if (border?.Child is ScrollViewer scrollViewer)
                {
                    if (e.Delta < 0)
                        scrollViewer.LineRight();
                    else
                        scrollViewer.LineLeft();
                    e.Handled = true;
                }
            }
        }

        private void LstFilmStrip_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (_selectedFrame != null)
            {
                RbViewSingle.IsChecked = true;
                ViewMode_Changed(sender, e);
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

        private void CmbExportFormat_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
        }

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
                    string format = "JPEG";
                    if (CmbExportFormat?.SelectedItem is ComboBoxItem item)
                    {
                        string text = item.Content?.ToString() ?? "";
                        if (text.Contains("TIFF", StringComparison.OrdinalIgnoreCase)) format = "TIFF";
                        else if (text.Contains("PNG", StringComparison.OrdinalIgnoreCase)) format = "PNG";
                    }

                    var options = new ExportOptions
                    {
                        OutputDirectory = targetFolder,
                        Format = format
                    };

                    await _exportService.ExportToFolderAsync(_currentRoll, options, progress);
                    MessageBox.Show($"フォルダへの一括書き出しが完了しました:\n{targetFolder}\n出力形式: {format}", "書き出し成功", MessageBoxButton.OK, MessageBoxImage.Information);

                    if (ChkOpenFolderAfterExport?.IsChecked == true && Directory.Exists(targetFolder))
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = targetFolder,
                            UseShellExecute = true
                        });
                    }
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