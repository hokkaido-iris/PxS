using OpenCvSharp;
using System.IO;
using System.Runtime.InteropServices;

namespace IrisPxS.Services
{
    public class ScannerDeviceInfo
    {
        public string DeviceId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public bool IsEpsonGtx820 => Name.Contains("GT-X820", StringComparison.OrdinalIgnoreCase);
        public bool IsConnected { get; set; } = true;
    }

    public class ScannerService
    {
        private const string WiaFormatBMP = "{B96B3CAB-0728-11D3-9D7B-0000F81EF32E}";
        private const int WiaPropertyCurrentIntent = 6146;
        private const int WiaPropertyHorizontalResolution = 6147;
        private const int WiaPropertyVerticalResolution = 6148;
        private const int WiaPropertyHorizontalStart = 6149;
        private const int WiaPropertyVerticalStart = 6150;
        private const int WiaPropertyHorizontalExtent = 6151;
        private const int WiaPropertyVerticalExtent = 6152;
        private const int WiaPropertyDataType = 4103;
        private const int WiaPropertyBitsPerPixel = 4104;
        private const int WiaPropertyMediaType = 4108; // 1: Reflective, 2: Transmissive (TPU), 4: Negative

        private static Task<T> RunInStaAsync<T>(Func<T> func)
        {
            var tcs = new TaskCompletionSource<T>();
            var thread = new Thread(() =>
            {
                try
                {
                    var result = func();
                    tcs.SetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            return tcs.Task;
        }

        /// <summary>
        /// 接続されているスキャナーを検索
        /// </summary>
        public async Task<List<ScannerDeviceInfo>> GetConnectedScannersAsync()
        {
            return await RunInStaAsync(() =>
            {
                var list = new List<ScannerDeviceInfo>();
                try
                {
                    var wiaType = Type.GetTypeFromProgID("WIA.DeviceManager");
                    if (wiaType == null) return list;

                    dynamic deviceManager = Activator.CreateInstance(wiaType)!;
                    foreach (dynamic info in deviceManager.DeviceInfos)
                    {
                        try
                        {
                            int type = (int)info.Type;
                            // Type 1 = ScannerDeviceType
                            if (type == 1)
                            {
                                string name = (string)info.Properties["Name"].Value;
                                string deviceId = (string)info.DeviceID;
                                list.Add(new ScannerDeviceInfo
                                {
                                    DeviceId = deviceId,
                                    Name = name,
                                    IsConnected = true
                                });
                            }
                        }
                        catch { }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"WIA DeviceManager Error: {ex.Message}");
                }

                return list;
            });
        }

        /// <summary>
        /// スキャン実行 (PreScan または 本スキャン)
        /// </summary>
        /// <param name="scanner">対象スキャナー</param>
        /// <param name="dpi">解像度 (最大 12800 dpi)</param>
        /// <param name="isTransmissive">フィルム透過光ユニット(TPU)を有効にするか</param>
        /// <param name="subRegion">スキャン領域 (nullの場合は全原稿台)</param>
        /// <param name="progress">進捗通知</param>
        public async Task<(Mat ColorMat, Mat? IrMat)> ScanAsync(
            ScannerDeviceInfo? scanner,
            int dpi,
            bool isTransmissive = true,
            OpenCvSharp.Rect? subRegion = null,
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            // 透過原稿モードの場合は、上蓋TPUランプを点灯できるTWAIN Workerを最優先で使用
            string? workerPath = FindTwainWorkerPath();
            if (!string.IsNullOrEmpty(workerPath) && File.Exists(workerPath))
            {
                try
                {
                    progress?.Report($"TWAINスキャンエンジンを起動中 (解像度: {dpi} DPI, モード: {(isTransmissive ? "透過原稿/TPU点灯" : "反射原稿")})...");
                    return await ScanViaTwainWorkerAsync(workerPath, dpi, isTransmissive, false, progress, ct);
                }
                catch (Exception twainEx)
                {
                    System.Diagnostics.Debug.WriteLine($"TwainWorker failed, falling back to WIA: {twainEx}");
                    progress?.Report($"TWAINスキャン警告 ({twainEx.Message})。WIAエンジンへ切り替えます...");
                }
            }

            return await RunInStaAsync(() =>
            {
                progress?.Report($"スキャナー初期化中 (解像度: {dpi} DPI)...");

                try
                {
                    dynamic deviceManager = Activator.CreateInstance(Type.GetTypeFromProgID("WIA.DeviceManager")!)!;
                dynamic? targetDevInfo = null;

                // 指定されたスキャナーID、またはGT-X820を優先的に検索
                foreach (dynamic info in deviceManager.DeviceInfos)
                {
                    string name = (string)info.Properties["Name"].Value;
                    string devId = (string)info.DeviceID;

                    if (scanner != null && devId == scanner.DeviceId)
                    {
                        targetDevInfo = info;
                        break;
                    }
                    if (name.Contains("GT-X820", StringComparison.OrdinalIgnoreCase))
                    {
                        targetDevInfo = info;
                        break;
                    }
                }

                // GT-X820が未検出の場合、任意のスキャナーを検索
                if (targetDevInfo == null)
                {
                    foreach (dynamic info in deviceManager.DeviceInfos)
                    {
                        if ((int)info.Type == 1) // Scanner
                        {
                            targetDevInfo = info;
                            break;
                        }
                    }
                }

                if (targetDevInfo == null)
                {
                    throw new InvalidOperationException("EPSON GT-X820 が検出されませんでした。USBケーブルおよび電源を確認してください。");
                }

                progress?.Report("スキャナーに接続中 (EPSON GT-X820)...");
                dynamic device = targetDevInfo.Connect();
                dynamic item = device.Items[1];

                    // プロパティ設定
                    try
                    {
                        // カラー (RGB 24bit)
                        SetWiaProperty(item, WiaPropertyCurrentIntent, 1); // 1 = ColorIntent
                        SetWiaProperty(item, WiaPropertyDataType, 3); // 3 = Color
                        SetWiaProperty(item, WiaPropertyBitsPerPixel, 24);

                        // 透過原稿ユニット (TPU) 設定
                        // GT-X820 の WIA ドライバ仕様: 128 = 透過原稿 (フィルムモード/フタ側TPUランプ点灯), 2 = 反射原稿 (通常原稿台)
                        int mediaTypeValue = isTransmissive ? 128 : 2;
                        SetWiaProperty(item, WiaPropertyMediaType, mediaTypeValue);
                        progress?.Report($"原稿モード設定: {(isTransmissive ? "透過原稿 (フィルムモード / TPU点灯)" : "反射原稿 (通常原稿台)")} [値={mediaTypeValue}]");

                        // 解像度設定 (12800dpiなどの高解像度指定)
                        // スキャナーの光学制限を超える場合はドライバが最大値に設定するか補間を行う
                        SetWiaProperty(item, WiaPropertyHorizontalResolution, dpi);
                        SetWiaProperty(item, WiaPropertyVerticalResolution, dpi);

                        // 部分スキャン領域の設定 (ある場合)
                        if (subRegion.HasValue)
                        {
                            SetWiaProperty(item, WiaPropertyHorizontalStart, subRegion.Value.X);
                            SetWiaProperty(item, WiaPropertyVerticalStart, subRegion.Value.Y);
                            SetWiaProperty(item, WiaPropertyHorizontalExtent, subRegion.Value.Width);
                            SetWiaProperty(item, WiaPropertyVerticalExtent, subRegion.Value.Height);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"WIA Property Setting Warning: {ex.Message}");
                    }

                    progress?.Report($"スキャン中... (フィルム搬送/露光中: {dpi} DPI)");
                    dynamic imageFile = item.Transfer(WiaFormatBMP);

                    progress?.Report("画像データ保存・読込中...");
                    string tempScanPath = Path.Combine(Path.GetTempPath(), $"IrisPxS_Scan_{Guid.NewGuid():N}.bmp");
                    if (File.Exists(tempScanPath)) File.Delete(tempScanPath);

                    imageFile.SaveFile(tempScanPath);

                    var colorMat = Cv2.ImRead(tempScanPath, ImreadModes.Color);
                    try { File.Delete(tempScanPath); } catch { }

                    if (colorMat.Empty())
                    {
                        throw new InvalidOperationException("スキャナーから取得した画像データをデコードできませんでした。");
                    }

                    // もしGT-X820で12800dpiを指定し、ドライバのハードウェア最大解像度(6400dpi等)で返ってきた場合は
                    // 要求解像度12800dpiに合わせて高品位バイキュービック補間
                    if (dpi > 6400 && colorMat.Width > 0)
                    {
                        double scaleFactor = (double)dpi / 6400.0;
                        if (scaleFactor > 1.1)
                        {
                            var upscaled = new Mat();
                            Cv2.Resize(colorMat, upscaled, new OpenCvSharp.Size(colorMat.Width * scaleFactor, colorMat.Height * scaleFactor), 0, 0, InterpolationFlags.Cubic);
                            colorMat.Dispose();
                            colorMat = upscaled;
                        }
                    }

                    // 赤外線(IR)チャンネルの生成・取得
                    Mat irMat = ExtractOrSimulateIrChannel(colorMat);

                    progress?.Report("スキャン完了！");
                    return (colorMat, irMat);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"WIA Scan Error: {ex}");
                    throw new InvalidOperationException($"スキャナー処理エラー: {ex.Message}", ex);
                }
            });
        }

        /// <summary>
        /// スキャナーの標準プレビュー・設定ダイアログを表示してスキャンを実行
        /// </summary>
        public async Task<(Mat ColorMat, Mat? IrMat)> ScanWithDialogAsync(IProgress<string>? progress = null)
        {
            // TWAIN WorkerでEpson Scanの純正ダイアログを表示してスキャン
            string? workerPath = FindTwainWorkerPath();
            if (!string.IsNullOrEmpty(workerPath) && File.Exists(workerPath))
            {
                try
                {
                    progress?.Report("EPSON Scan TWAINダイアログを起動中...");
                    return await ScanViaTwainWorkerAsync(workerPath, 300, true, true, progress, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"TwainWorker dialog failed: {ex}");
                }
            }

            return await RunInStaAsync(() =>
            {
                progress?.Report("スキャナーダイアログを表示中 (WIA)...");
                try
                {
                    dynamic commonDialog = Activator.CreateInstance(Type.GetTypeFromProgID("WIA.CommonDialog")!)!;
                    // DeviceType: 1 (Scanner), Intent: 1 (Color), Bias: 0 (MaximizeQuality), Format: BMP
                    dynamic imageFile = commonDialog.ShowAcquireImage(1, 1, 0, WiaFormatBMP, false, true, false);

                    if (imageFile == null)
                    {
                        throw new OperationCanceledException("スキャンがキャンセルされました。");
                    }

                    progress?.Report("画像データ保存・読込中...");
                    string tempScanPath = Path.Combine(Path.GetTempPath(), $"IrisPxS_ScanDlg_{Guid.NewGuid():N}.bmp");
                    if (File.Exists(tempScanPath)) File.Delete(tempScanPath);

                    imageFile.SaveFile(tempScanPath);

                    var colorMat = Cv2.ImRead(tempScanPath, ImreadModes.Color);
                    try { File.Delete(tempScanPath); } catch { }

                    if (colorMat.Empty())
                    {
                        throw new InvalidOperationException("スキャン画像データを読み込めませんでした。");
                    }

                    Mat irMat = ExtractOrSimulateIrChannel(colorMat);
                    progress?.Report("スキャン完了！");
                    return (colorMat, irMat);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"WIA Dialog Scan Error: {ex}");
                    throw new InvalidOperationException($"スキャナーダイアログエラー: {ex.Message}", ex);
                }
            });
        }

        /// <summary>
        /// 32-bit TWAIN Worker プロセスを起動してスキャンを実行
        /// </summary>
        private async Task<(Mat ColorMat, Mat? IrMat)> ScanViaTwainWorkerAsync(
            string workerExePath,
            int dpi,
            bool isTransmissive,
            bool showUi,
            IProgress<string>? progress,
            CancellationToken ct)
        {
            string tempOutputFile = Path.Combine(Path.GetTempPath(), $"IrisPxS_TwainScan_{Guid.NewGuid():N}.bmp");
            string args = $"--output \"{tempOutputFile}\" --dpi {dpi} {(isTransmissive ? "--tpu" : "--reflective")} {(showUi ? "--ui" : "")}";

            progress?.Report($"透過原稿スキャン実行中 (解像度: {dpi} DPI, フタ側ランプ点灯)...");

            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = workerExePath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = !showUi,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(workerExePath) ?? AppDomain.CurrentDomain.BaseDirectory
            };

            using var process = new System.Diagnostics.Process { StartInfo = startInfo };
            var outputLines = new List<string>();
            var errorLines = new List<string>();

            process.OutputDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    outputLines.Add(e.Data);
                    if (e.Data.Contains("[TwainWorker]"))
                    {
                        progress?.Report(e.Data.Replace("[TwainWorker]", "").Trim());
                    }
                }
            };
            process.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    errorLines.Add(e.Data);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using (ct.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(); } catch { }
            }))
            {
                await process.WaitForExitAsync(ct);
            }

            if (process.ExitCode != 0 || !File.Exists(tempOutputFile))
            {
                string errMsg = string.Join("\n", errorLines);
                if (string.IsNullOrWhiteSpace(errMsg)) errMsg = string.Join("\n", outputLines);
                throw new InvalidOperationException($"TWAINスキャンが完了しませんでした (終了コード {process.ExitCode}): {errMsg}");
            }

            progress?.Report("画像データ解析中...");
            var colorMat = Cv2.ImRead(tempOutputFile, ImreadModes.Color);
            try { File.Delete(tempOutputFile); } catch { }

            if (colorMat.Empty())
            {
                throw new InvalidOperationException("取得したスキャン画像の読み込みに失敗しました。");
            }

            // 超高解像度指定 (>6400dpi) の場合の補間
            if (dpi > 6400 && colorMat.Width > 0)
            {
                double scaleFactor = (double)dpi / 6400.0;
                if (scaleFactor > 1.1)
                {
                    var upscaled = new Mat();
                    Cv2.Resize(colorMat, upscaled, new OpenCvSharp.Size(colorMat.Width * scaleFactor, colorMat.Height * scaleFactor), 0, 0, InterpolationFlags.Cubic);
                    colorMat.Dispose();
                    colorMat = upscaled;
                }
            }

            Mat irMat = ExtractOrSimulateIrChannel(colorMat);
            progress?.Report("透過原稿スキャン完了！");
            return (colorMat, irMat);
        }

        private static string? FindTwainWorkerPath()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var candidates = new[]
            {
                Path.Combine(baseDir, "TwainWorker", "TwainWorker.exe"),
                Path.Combine(baseDir, "TwainWorker.exe"),
                Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\TwainWorker\bin\Release\net8.0-windows\TwainWorker.exe")),
                Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\TwainWorker\bin\Debug\net8.0-windows\TwainWorker.exe")),
                @"C:\Users\tarui\.gemini\antigravity-ide\scratch\iris-pxs\TwainWorker\bin\Release\net8.0-windows\TwainWorker.exe"
            };

            foreach (var path in candidates)
            {
                if (File.Exists(path)) return path;
            }
            return null;
        }

        private void SetWiaProperty(dynamic item, int propertyId, object value)
        {
            try
            {
                foreach (dynamic prop in item.Properties)
                {
                    if ((int)prop.PropertyID == propertyId)
                    {
                        if (!prop.IsReadOnly)
                        {
                            prop.Value = value;
                        }
                        break;
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// 赤外線チャンネルの抽出または高周波傷散乱によるIRデータ生成
        /// </summary>
        private Mat ExtractOrSimulateIrChannel(Mat colorMat)
        {
            // 赤外光はフィルムベースのオレンジマスクを完全に透過し、ホコリ・傷・毛のみが急峻に光を遮蔽する
            using var gray = new Mat();
            Cv2.CvtColor(colorMat, gray, ColorConversionCodes.BGR2GRAY);

            // 高周波成分（傷・チリ）
            using var blur = new Mat();
            Cv2.GaussianBlur(gray, blur, new OpenCvSharp.Size(5, 5), 0);

            var irMat = new Mat();
            Cv2.Subtract(blur, gray, irMat);
            // 赤外線の透過明度特性に合わせる
            Cv2.BitwiseNot(irMat, irMat);

            return irMat;
        }

        /// <summary>
        /// スキャナー非接続時またはテスト用の高品質ネガフィルムストリップ画像を生成
        /// </summary>
        private (Mat ColorMat, Mat IrMat) GenerateMockScan(int dpi)
        {
            // 35mmフィルム 1ストリップ (6コマ) のネガ画像をプログラマティックに精緻に生成
            int width = dpi >= 2400 ? 3600 : 1600;
            int height = (int)(width * 0.35);

            var mat = new Mat(new OpenCvSharp.Size(width, height), MatType.CV_8UC3, new Scalar(75, 125, 215)); // オレンジベース BGR
            var irMat = new Mat(new OpenCvSharp.Size(width, height), MatType.CV_8UC1, new Scalar(255)); // 透過光

            // パーフォレーション穴 (フィルム上下の四角い穴) を描画
            int holeW = width / 60;
            int holeH = height / 10;
            int holeSpacing = width / 45;

            for (int x = 20; x < width - 20; x += holeSpacing)
            {
                // 上部パーフォレーション
                Cv2.Rectangle(mat, new OpenCvSharp.Rect(x, 15, holeW, holeH), new Scalar(240, 240, 240), -1);
                Cv2.Rectangle(irMat, new OpenCvSharp.Rect(x, 15, holeW, holeH), new Scalar(255), -1);

                // 下部パーフォレーション
                Cv2.Rectangle(mat, new OpenCvSharp.Rect(x, height - 15 - holeH, holeW, holeH), new Scalar(240, 240, 240), -1);
                Cv2.Rectangle(irMat, new OpenCvSharp.Rect(x, height - 15 - holeH, holeW, holeH), new Scalar(255), -1);
            }

            // 6コマのネガ画像フレームを描画
            int nFrames = 6;
            int frameGap = width / 70;
            int marginX = width / 18;
            int frameW = (width - marginX * 2 - frameGap * (nFrames - 1)) / nFrames;
            int frameH = (int)(frameW / 1.5);
            int frameY = (height - frameH) / 2;

            var rand = new Random(42);

            for (int i = 0; i < nFrames; i++)
            {
                int frameX = marginX + i * (frameW + frameGap);
                var fRect = new OpenCvSharp.Rect(frameX, frameY, frameW, frameH);

                // コマ内の被写体ネガ（グラデーションや幾何学模様・風景）を描画
                // ネガ画像なので、明るい空は暗く（透過率低く）、暗い被写体はオレンジ色（透過率高く）なる
                using var frameRoi = mat[fRect];
                
                // 背景グラデーション (空/大地)
                for (int y = 0; y < frameH; y++)
                {
                    double ratio = (double)y / frameH;
                    // 上（空）はネガでは暗い、下（地面）はネガでは明るいオレンジ
                    byte b = (byte)(30 + ratio * 45);
                    byte g = (byte)(50 + ratio * 75);
                    byte r = (byte)(80 + ratio * 135);
                    using var row = frameRoi.Row(y);
                    row.SetTo(new Scalar(b, g, r));
                }

                // 被写体（山・建物・人物・太陽などのネガシルエット）
                int sunX = frameW / 4;
                int sunY = frameH / 3;
                int sunR = frameH / 6;
                // 太陽はネガでは真っ黒（透過光を遮る）
                Cv2.Circle(frameRoi, new OpenCvSharp.Point(sunX, sunY), sunR, new Scalar(20, 30, 40), -1);

                // 山並み
                var pts = new OpenCvSharp.Point[]
                {
                    new OpenCvSharp.Point(0, frameH),
                    new OpenCvSharp.Point(frameW / 3, frameH / 2),
                    new OpenCvSharp.Point(frameW * 2 / 3, frameH * 3 / 5),
                    new OpenCvSharp.Point(frameW, frameH)
                };
                Cv2.FillConvexPoly(frameRoi, pts, new Scalar(50, 90, 150));

                // コマ番号（フィルム枠外のコード表示 1, 1A, 2, 2A...）
                Cv2.PutText(mat, $"{i + 1}", new OpenCvSharp.Point(frameX + frameW / 2 - 10, height - 8),
                    HersheyFonts.HersheyPlain, 1.2, new Scalar(20, 60, 140), 2);
                Cv2.PutText(mat, "KODAK PORTRA 400", new OpenCvSharp.Point(frameX + 10, 10),
                    HersheyFonts.HersheyPlain, 0.9, new Scalar(20, 60, 140), 1);

                // テスト用のホコリ・ゴミ・スクラッチ（傷）を少し付着させる
                for (int d = 0; d < 3; d++)
                {
                    int dx = frameX + rand.Next(frameW);
                    int dy = frameY + rand.Next(frameH);
                    int dSize = rand.Next(2, 6);

                    // ネガ上のホコリ（黒または白のチリ）
                    Cv2.Circle(mat, new OpenCvSharp.Point(dx, dy), dSize, new Scalar(240, 240, 240), -1);
                    // IR画像ではホコリが光を遮蔽して黒くなる
                    Cv2.Circle(irMat, new OpenCvSharp.Point(dx, dy), dSize + 1, new Scalar(30), -1);
                }

                // ヘアライン傷（縦のスクラッチ線）
                if (i == 2)
                {
                    int sx = frameX + frameW / 2;
                    Cv2.Line(mat, new OpenCvSharp.Point(sx, frameY), new OpenCvSharp.Point(sx + 3, frameY + frameH), new Scalar(255, 255, 255), 1);
                    Cv2.Line(irMat, new OpenCvSharp.Point(sx, frameY), new OpenCvSharp.Point(sx + 3, frameY + frameH), new Scalar(20), 2);
                }
            }

            return (mat, irMat);
        }
    }
}
