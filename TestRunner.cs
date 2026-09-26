using System.IO;
using IrisPxS.Models;
using IrisPxS.Services;
using OpenCvSharp;

namespace IrisPxS
{
    public static class TestRunner
    {
        public static async Task RunVerificationAsync()
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine("=================================================================");
            Console.WriteLine(" IRIS PxS Verification Test Suite");
            Console.WriteLine("=================================================================");

            var scannerService = new ScannerService();
            var negativeEngine = new FilmNegativeEngine();
            var detectorService = new FrameDetectorService();
            var dustService = new DustScratchRemovalService();
            var exifService = new ExifMetadataService();
            var sessionService = new RollSessionService();
            var exportService = new RollExportService(negativeEngine, dustService, exifService);

            // 1. スキャナー接続テスト
            Console.WriteLine("\n[1/6] スキャナー検索テスト...");
            var scanners = await scannerService.GetConnectedScannersAsync();
            Console.WriteLine($"検出スキャナー数: {scanners.Count}");
            foreach (var sc in scanners)
            {
                Console.WriteLine($" - {sc.Name} (ID: {sc.DeviceId}, Epson: {sc.IsEpsonGtx820})");
            }

            // 2. モック/実機スキャンテスト (300dpi PreScan)
            Console.WriteLine("\n[2/6] フィルムストリップ生成・スキャンテスト...");
            Mat colorMat;
            Mat? irMat;
            try
            {
                (colorMat, irMat) = await scannerService.ScanAsync(null, 300, true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"実機スキャナー警告 ({ex.Message})。実スキャン画像 (real_scan.bmp) にフォールバックします。");
                string fallbackPath = @"C:\Users\tarui\.gemini\antigravity-ide\scratch\real_scan.bmp";
                if (File.Exists(fallbackPath))
                {
                    colorMat = Cv2.ImRead(fallbackPath);
                    irMat = colorMat.Clone();
                }
                else
                {
                    colorMat = new Mat(2861, 809, MatType.CV_8UC3, new Scalar(240, 240, 240));
                    irMat = null;
                }
            }
            Console.WriteLine($"スキャン取得サイズ: {colorMat.Width}x{colorMat.Height}, IRサイズ: {irMat?.Width}x{irMat?.Height}");

            // 3. コマ自動検出テスト (35mm フルサイズ & 傾き補正)
            Console.WriteLine("\n[3/6] コマ自動認識テスト (35mm Full Format & 傾き補正)...");
            var format = FilmFormat.GetPresetFormats()[0]; // 135 Full-Frame
            var (straightenedMat, skewAngle, detectedFrames) = detectorService.DetectAndStraighten(colorMat, format, 300);
            Console.WriteLine($"直立画像検知傾き角: {skewAngle:F2}°, 検出コマ数: {detectedFrames.Count}");
            FrameDetectorService.GetFormatDimensions(format, straightenedMat.Height >= straightenedMat.Width, 300, out int expW, out int expH, out _);
            double expectedAspect = (double)expH / expW;
            for (int i = 0; i < detectedFrames.Count; i++)
            {
                var r = detectedFrames[i];
                double aspect = (double)r.Height / r.Width;
                Console.WriteLine($"  コマ #{i + 1}: X={r.X}, Y={r.Y}, W={r.Width}, H={r.Height}, Aspect={aspect:F2}");
                if (Math.Abs(r.Width - expW) > expW * 0.15 || Math.Abs(r.Height - expH) > expH * 0.15)
                {
                    throw new Exception($"[FAIL] コマ寸法が許容範囲外: 期待値 W={expW}, H={expH} に対し 実際 W={r.Width}, H={r.Height}");
                }
                if (Math.Abs(aspect - expectedAspect) > expectedAspect * 0.20)
                {
                    throw new Exception($"[FAIL] アスペクト比が許容範囲外: 期待値 {expectedAspect:F2} に対し 実際 {aspect:F2}");
                }
            }
            Console.WriteLine($"  => 全 {detectedFrames.Count} コマが適正アスペクト比・サイズ範囲内に自動微調整・配置されました。[PASS]");

            // 実スキャン画像 (real_scan.bmp) がある場合の高精度検証
            string realScanPath = @"C:\Users\tarui\.gemini\antigravity-ide\scratch\real_scan.bmp";
            if (File.Exists(realScanPath))
            {
                Console.WriteLine("\n[実機スキャン画像検証 (real_scan.bmp)]...");
                using var realMat = Cv2.ImRead(realScanPath);
                Console.WriteLine($"real_scan.bmp サイズ: 幅={realMat.Width}, 高さ={realMat.Height}, Channels={realMat.Channels()}");

                // 1. 135 Full-Frame 自動認識テスト (等間隔ベースグリッド ＋ 局所エッジ・プロファイル自動微調整)
                double rawSkew = detectorService.DetectFilmSkewAngleFromMediaBoundary(realMat);
                Console.WriteLine($"[real_scan.bmp 傾き検知] 検出傾き角: {rawSkew:F2}°");

                var (straight135, skew135, frames135) = detectorService.DetectAndStraighten(realMat, format, 300);
                Console.WriteLine($"\n[135-FF] 検知傾き角: {skew135:F2}°, 検出コマ数: {frames135.Count}");
                FrameDetectorService.GetFormatDimensions(format, straight135.Height >= straight135.Width, 300, out int expW135, out int expH135, out _);
                double expAspect135 = (double)expH135 / expW135;
                for (int i = 0; i < frames135.Count; i++)
                {
                    var r = frames135[i];
                    Console.WriteLine($"  135微調整コマ #{i + 1}: X={r.X}, Y={r.Y}, W={r.Width}, H={r.Height} (Y範囲: {r.Y}〜{r.Y + r.Height}, Aspect={(double)r.Height / r.Width:F2})");
                    if (r.Width != expW135 || r.Height != expH135)
                    {
                        throw new Exception($"[FAIL] 135コマ寸法が不一致: 期待値 W={expW135}, H={expH135} に対し 実際 W={r.Width}, H={r.Height}");
                    }
                    double aspect = (double)r.Height / r.Width;
                    if (Math.Abs(aspect - expAspect135) > 1e-4)
                    {
                        throw new Exception($"[FAIL] 135アスペクト比がデフォルト値と不一致: 期待値 {expAspect135:F4} に対し 実際 {aspect:F4}");
                    }
                }
                Console.WriteLine($"  => [135-FF] 全 {frames135.Count} コマの比率は完全に不変であり、デフォルト比率 ({expAspect135:F2}: W={expW135}, H={expH135}) を維持しています。[PASS]");

                // 2. 110 General 自動認識テスト
                var format110 = FilmFormat.GetAllFormats().First(f => f.Type == FilmFormatType.Format110_General);
                string real110Path = @"C:\Users\tarui\AppData\Local\IrisPxS\Sessions\3af5cddf97314f388b106efe456f32cc\Strips\710a02b920bd47eb97a94d14ccf6a8bd_raw.png";
                if (!File.Exists(real110Path))
                {
                    real110Path = @"C:\Users\tarui\AppData\Local\IrisPxS\Sessions\13fb9715ab084ca3b565923566cf3420\Strips\d4464c14b2fb4697a2f0fe24b712aba7_raw.png";
                }
                Mat target110Mat;
                int testDpi = 300;
                if (File.Exists(real110Path))
                {
                    using var full110 = Cv2.ImRead(real110Path);
                    if (full110.Width > 2000)
                    {
                        double sc = 300.0 / 2400.0;
                        target110Mat = new Mat();
                        Cv2.Resize(full110, target110Mat, new OpenCvSharp.Size((int)(full110.Width * sc), (int)(full110.Height * sc)));
                    }
                    else
                    {
                        target110Mat = full110.Clone();
                    }
                }
                else
                {
                    target110Mat = realMat.Clone();
                    testDpi = 300;
                }

                using (target110Mat)
                {
                    var (straight110, skew110, frames110) = detectorService.DetectAndStraighten(target110Mat, format110, testDpi);
                    Console.WriteLine($"\n[110-General] 検知傾き角: {skew110:F2}°, 検出コマ数: {frames110.Count}");
                    FrameDetectorService.GetFormatDimensions(format110, straight110.Height >= straight110.Width, testDpi, out int expW110, out int expH110, out _);
                    double expAspect110 = (double)expH110 / expW110;
                    for (int i = 0; i < frames110.Count; i++)
                    {
                        var r = frames110[i];
                        Console.WriteLine($"  110コマ #{i + 1}: X={r.X}, Y={r.Y}, W={r.Width}, H={r.Height} (Y範囲: {r.Y}〜{r.Y + r.Height}, Aspect={(double)r.Height / r.Width:F2})");
                        if (r.Width != expW110 || r.Height != expH110)
                        {
                            throw new Exception($"[FAIL] 110コマ寸法が不一致: 期待値 W={expW110}, H={expH110} に対し 実際 W={r.Width}, H={r.Height}");
                        }
                        double aspect = (double)r.Height / r.Width;
                        if (Math.Abs(aspect - expAspect110) > 1e-4)
                        {
                            throw new Exception($"[FAIL] 110アスペクト比がデフォルト値と不一致: 期待値 {expAspect110:F4} に対し 実際 {aspect:F4}");
                        }
                    }
                    if (File.Exists(real110Path) && frames110.Count != 8)
                    {
                        throw new Exception($"[FAIL] 実機110画像の検出コマ数が8コマではありませんでした: 実際={frames110.Count}");
                    }
                    Console.WriteLine($"  => [110-General] 全 {frames110.Count} コマが実機パーフォレーション穴基準でジャストフィット検出され、比率不変 ({expAspect110:F2}: W={expW110}, H={expH110}) を維持しています。[PASS]");
                }
            }

            // フィルムとメディアなし部分のコントラストによる大角度傾き検出テスト (+2.5度, +6.5度, -8.5度)
            double[] testAngles = { 2.5, 6.5, -8.5 };
            foreach (var testAng in testAngles)
            {
                using var rotMat = Cv2.GetRotationMatrix2D(new Point2f(colorMat.Width / 2f, colorMat.Height / 2f), -testAng, 1.0);
                using var tiltedColor = new Mat();
                Cv2.WarpAffine(colorMat, tiltedColor, rotMat, colorMat.Size(), InterpolationFlags.Linear, BorderTypes.Constant, new Scalar(250, 250, 250));
                
                double detectedHoughAngle = detectorService.DetectFilmSkewAngleFromMediaBoundary(tiltedColor);
                double expectedAngle = skewAngle - testAng;
                Console.WriteLine($"[傾き検知] 意図的付加傾き ({testAng:+0.00;-0.00}°): 期待値 {expectedAngle:+0.00;-0.00}° に対し検知 {detectedHoughAngle:+0.00;-0.00}° (残差: {Math.Abs(detectedHoughAngle - expectedAngle):F2}°)");
            }

            // 4. ベースカラー自動検知 & NP変換テスト
            Console.WriteLine("\n[4/6] ベースカラー自動検知 & NP変換テスト...");
            var baseColor = negativeEngine.DetectBaseColor(colorMat, detectedFrames[0]);
            Console.WriteLine($"検知されたベースカラー: R={baseColor.R}, G={baseColor.G}, B={baseColor.B}");

            var frame = new FilmFrame
            {
                FrameNumber = 1,
                CropRect = detectedFrames[0],
                BaseColorR = baseColor.R,
                BaseColorG = baseColor.G,
                BaseColorB = baseColor.B,
                ProfileId = "portra400",
                Exposure = 0.2,
                Contrast = 1.1,
                CameraMake = "Leica",
                CameraModel = "M6",
                LensModel = "Summicron 50mm f/2",
                FNumber = 2.0,
                ShutterSpeed = "1/500",
                ISO = 400
            };

            using var frameRoi = colorMat[frame.CropRect];
            using var positiveMat = negativeEngine.ConvertNegativeToPositive(frameRoi, frame);
            Console.WriteLine($"NP変換完了: サイズ {positiveMat.Width}x{positiveMat.Height}");

            // 4.5 自動トーン補正 (Auto Tone) テスト
            Console.WriteLine("\n[4.5/6] 自動トーン補正 (Auto Tone) アルゴリズムテスト...");
            var autoTone = negativeEngine.CalculateAutoTone(colorMat, frame);
            Console.WriteLine($"自動トーン補正結果: EV={autoTone.Exposure:+0.00;-0.00}, Contrast={autoTone.Contrast:F2}, Saturation={autoTone.Saturation:F2}, Temp={autoTone.ColorTemp:+0.0;-0.0}, Tint={autoTone.Tint:+0.0;-0.0}");

            // 5. 赤外線ゴミ・キズ除去 (Digital ICE) テスト
            Console.WriteLine("\n[5/6] 赤外線ゴミ・キズ除去テスト...");
            using var irRoi = irMat?[frame.CropRect];
            using var defectMask = irRoi != null
                ? dustService.GenerateDefectMaskFromIr(irRoi, 2)
                : dustService.GenerateDefectMaskFromColor(frameRoi, 2);
            using var cleanedMat = dustService.RemoveDustAndScratches(frameRoi, defectMask);
            Console.WriteLine($"ゴミ除去完了: 欠陥非ゼロ画素数 = {Cv2.CountNonZero(defectMask)}");

            // 6. 複数カット（マルチストリップ）ワークフロー ＆ 一括フォルダ/ZIPエクスポートテスト
            Console.WriteLine("\n[6/6] 複数カットワークフロー (Cut 1 PreScan/Scan -> Cut 2 -> 一括書き出し) テスト...");
            var roll = new RollSession
            {
                RollName = "TestRoll_MultiCut_01",
                FilmStock = "Kodak Portra 400",
                DefaultCamera = "Leica M6",
                DefaultLens = "Summicron 50mm f/2"
            };

            // Cut 1: PreScan (300dpi) で枠決定 -> Scan (2400dpi) で枠引き継ぎ
            var cut1 = new FilmStrip { StripIndex = 1, Name = "Cut 1", Status = StripStatus.PreScanned, ScanDpi = 300 };
            
            // 意図的な傾き(-2.5度)を付加したシミュレーションでPreScanおよびMainScanの傾き補正挙動を検証
            double testTilt = -2.5;
            using var tiltedScan = detectorService.StraightenImage(colorMat, -testTilt);
            var (preStraight, preSkew, preFrames) = detectorService.DetectAndStraighten(tiltedScan, format, 300);
            cut1.SkewAngle = preSkew;
            cut1.FrameCoordinatesDpi = 300;
            Console.WriteLine($"[ワークフロー検証] PreScan 付加傾き: {testTilt:F1}°, 検知傾き: {preSkew:F2}°, 枠数: {preFrames.Count}");

            foreach (var r in preFrames)
            {
                cut1.Frames.Add(new FilmFrame
                {
                    FrameNumber = cut1.Frames.Count + 1,
                    StripId = cut1.Id,
                    CropRect = r,
                    BaseColorR = baseColor.R,
                    BaseColorG = baseColor.G,
                    BaseColorB = baseColor.B
                });
            }

            // MainScan (2400 DPI 相当: 8倍解像度) をシミュレート
            using var mainScanRaw = new Mat();
            Cv2.Resize(tiltedScan, mainScanRaw, new OpenCvSharp.Size(tiltedScan.Width * 2, tiltedScan.Height * 2), 0, 0, InterpolationFlags.Cubic);
            int mainDpi = 600; // 2倍DPIでシミュレーション

            // MainWindow.xaml.cs の ApplyScanDataToCut ロジックを正確にシミュレート
            Mat mainScanStraight;
            if (Math.Abs(cut1.SkewAngle) >= 0.1)
            {
                mainScanStraight = detectorService.StraightenImage(mainScanRaw, cut1.SkewAngle);
            }
            else
            {
                mainScanStraight = mainScanRaw.Clone();
            }

            Console.WriteLine($"[ワークフロー検証] MainScan補正前サイズ: {mainScanRaw.Width}x{mainScanRaw.Height}, 補正後: {mainScanStraight.Width}x{mainScanStraight.Height}");

            // コマ枠のスケーリング (300 -> 600 DPI: scale=2.0)
            double scale = (double)mainDpi / cut1.FrameCoordinatesDpi;
            for (int fi = 0; fi < cut1.Frames.Count; fi++)
            {
                var f = cut1.Frames[fi];
                var r = f.CropRect;
                int sx = (int)Math.Round(r.X * scale);
                int sy = (int)Math.Round(r.Y * scale);
                int sw = (int)Math.Round(r.Width * scale);
                int sh = (int)Math.Round(r.Height * scale);
                f.CropRect = new OpenCvSharp.Rect(sx, sy, sw, sh);
                Console.WriteLine($"  コマ #{fi + 1} スケーリング後: X={sx}, Y={sy}, W={sw}, H={sh} (補正後画像サイズ内: {sx >= 0 && sx + sw <= mainScanStraight.Width && sy >= 0 && sy + sh <= mainScanStraight.Height})");
            }
            mainScanStraight.Dispose();

            var (c1Raw, c1Ir) = sessionService.SaveStripImages(roll.SessionId, cut1.Id, colorMat, irMat);
            cut1.FullScanImagePath = c1Raw;
            cut1.FullScanIrPath = c1Ir;
            foreach (var f in cut1.Frames)
            {
                f.RawImagePath = c1Raw;
                f.IrImagePath = c1Ir;
            }
            cut1.Status = StripStatus.Scanned;
            cut1.ScanDpi = 2400;
            roll.Strips.Add(cut1);

            // Cut 2: 次のカットを追加してスキャン
            var cut2 = new FilmStrip { StripIndex = 2, Name = "Cut 2", Status = StripStatus.Scanned, ScanDpi = 2400 };
            var (c2Raw, c2Ir) = sessionService.SaveStripImages(roll.SessionId, cut2.Id, colorMat, irMat);
            cut2.FullScanImagePath = c2Raw;
            cut2.FullScanIrPath = c2Ir;

            var f3 = new FilmFrame
            {
                FrameNumber = 3,
                StripId = cut2.Id,
                CropRect = detectedFrames.Count > 2 ? detectedFrames[2] : detectedFrames[0],
                RawImagePath = c2Raw,
                BaseColorR = baseColor.R,
                BaseColorG = baseColor.G,
                BaseColorB = baseColor.B
            };
            cut2.Frames.Add(f3);
            roll.Strips.Add(cut2);

            // 全コマの同期・採番
            roll.AllFrames.Clear();
            int totalIdx = 1;
            foreach (var st in roll.Strips)
            {
                foreach (var fr in st.Frames)
                {
                    fr.FrameNumber = totalIdx++;
                    roll.AllFrames.Add(fr);
                }
            }
            Console.WriteLine($"ロール全体: カット数={roll.Strips.Count}, 合計コマ数={roll.AllFrames.Count}");
            foreach (var st in roll.Strips)
            {
                Console.WriteLine($" - {st.Name}: 状態={st.StatusText}, コマ数={st.Frames.Count}, DPI={st.ScanDpi}");
            }

            string testExportFolder = Path.Combine(Path.GetTempPath(), "IrisPxS_TestExport");
            string testZipPath = Path.Combine(Path.GetTempPath(), "IrisPxS_TestExport.zip");

            var exportOpt = new ExportOptions
            {
                OutputDirectory = testExportFolder,
                OutputZipPath = testZipPath,
                Format = "JPEG",
                GenerateContactSheet = true
            };

            var exportedFiles = await exportService.ExportToFolderAsync(roll, exportOpt);
            Console.WriteLine($"フォルダ書き出し完了: {exportedFiles.Count} ファイル");
            foreach (var f in exportedFiles) Console.WriteLine($" - {Path.GetFileName(f)} ({new FileInfo(f).Length / 1024} KB)");

            var zipPath = await exportService.ExportToZipAsync(roll, exportOpt);
            Console.WriteLine($"ZIP書き出し完了: {Path.GetFileName(zipPath)} ({new FileInfo(zipPath).Length / 1024} KB)");
        }

        public static async Task Run110DiagnosticAsync()
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine("=== 110 Frame Detection Diagnostic ===");

            string imgPath = @"C:\Users\tarui\AppData\Local\IrisPxS\Sessions\3af5cddf97314f388b106efe456f32cc\Strips\710a02b920bd47eb97a94d14ccf6a8bd_raw.png";
            if (!File.Exists(imgPath))
            {
                Console.WriteLine($"Image not found: {imgPath}");
                return;
            }

            using var scanMat = Cv2.ImRead(imgPath);
            Console.WriteLine($"Image loaded: {scanMat.Width}x{scanMat.Height}");

            var detector = new FrameDetectorService();
            var format = FilmFormat.GetAllFormats().First(f => f.Type == FilmFormatType.Format110_General);
            int dpi = 300;

            double rawSkew = detector.DetectFilmSkewAngleFromMediaBoundary(scanMat);
            Console.WriteLine($"Raw skew angle: {rawSkew:F2}°");

            FrameDetectorService.GetFormatDimensions(format, true, dpi, out int targetW, out int targetH, out int pitchPx);
            double stripTotalWidthMm = FrameDetectorService.GetFilmStripTotalWidthMm(format);
            int expectedStripPx = (int)Math.Round(stripTotalWidthMm * dpi / 25.4);
            Console.WriteLine($"Specs: targetW={targetW}, targetH={targetH}, pitchPx={pitchPx}, expectedStripPx={expectedStripPx}");

            var (filmLeft, filmRight) = detector.DetectFilmHorizontalEdges(scanMat, expectedStripPx);
            Console.WriteLine($"Film edges: Left={filmLeft}, Right={filmRight}, Width={filmRight - filmLeft}");

            using var gray = new Mat();
            Cv2.CvtColor(scanMat, gray, ColorConversionCodes.BGR2GRAY);
            double mmToPx = (double)dpi / 25.4;
            int searchMarginPx = (int)Math.Round(4.5 * mmToPx);
            int minHolePx = (int)Math.Max(5, Math.Round(0.6 * mmToPx));
            int maxHolePx = (int)Math.Round(3.5 * mmToPx);

            var leftHoles = detector.FindPerforationHolesInMargin(gray, Math.Max(0, filmLeft - 10), Math.Min(searchMarginPx, scanMat.Width - filmLeft), minHolePx, maxHolePx);
            var rightHoles = detector.FindPerforationHolesInMargin(gray, Math.Max(0, filmRight - searchMarginPx), Math.Min(searchMarginPx + 10, scanMat.Width - (filmRight - searchMarginPx)), minHolePx, maxHolePx);
            Console.WriteLine($"Left holes: {leftHoles.Count}, Right holes: {rightHoles.Count}");
            for (int i = 0; i < leftHoles.Count; i++)
                Console.WriteLine($"  Left hole #{i+1}: X={leftHoles[i].X}, Y={leftHoles[i].Y}, W={leftHoles[i].Width}, H={leftHoles[i].Height}");
            for (int i = 0; i < rightHoles.Count; i++)
                Console.WriteLine($"  Right hole #{i+1}: X={rightHoles[i].X}, Y={rightHoles[i].Y}, W={rightHoles[i].Width}, H={rightHoles[i].Height}");

            Console.WriteLine("\n--- Per-frame local analysis & Content Verification ---");
            for (int i = 0; i < leftHoles.Count; i++)
            {
                var h = leftHoles[i];
                int hCy = h.Y + h.Height / 2;
                int frameCy = hCy + 300 / 2;
                int topY = frameCy - targetH / 2;

                // 局所フィルム境界
                int subTop = Math.Max(0, topY);
                int subH = Math.Min(targetH, scanMat.Height - subTop);
                using var subRoi = new Mat(scanMat, new OpenCvSharp.Rect(0, subTop, scanMat.Width, subH));
                var (localLeft, localRight) = detector.DetectFilmHorizontalEdges(subRoi, expectedStripPx);

                // 各コマの実際の写真枠（Y方向のエッジ）を探索
                int perfRightEdge = h.X + h.Width;
                int rightMargin = (int)Math.Round(0.8 * mmToPx);
                int localFrameX = localRight - rightMargin - targetW;
                if (localFrameX < perfRightEdge + 2) localFrameX = perfRightEdge + 2;
                if (localFrameX + targetW > localRight) localFrameX = localRight - targetW;

                // Y方向の探索: topY の前後 ±40px で上下のエッジ（明暗境界）を探索
                int scanYStart = Math.Max(0, topY - 30);
                int scanYLen = Math.Min(scanMat.Height - scanYStart, targetH + 60);
                using var frameColRoi = new Mat(scanMat, new OpenCvSharp.Rect(localFrameX + (int)(targetW * 0.15), scanYStart, (int)(targetW * 0.70), scanYLen));
                using var colGray = new Mat();
                Cv2.CvtColor(frameColRoi, colGray, ColorConversionCodes.BGR2GRAY);
                using var rowMean = new Mat();
                Cv2.Reduce(colGray, rowMean, ReduceDimension.Column, ReduceTypes.Avg, MatType.CV_32F);
                float[] vals = new float[scanYLen];
                System.Runtime.InteropServices.Marshal.Copy(rowMean.Data, vals, 0, scanYLen);

                // 上下のエッジ探索
                int bestTopK = -1;
                float bestTopGrad = 0;
                for (int k = 5; k < 55; k++)
                {
                    float g = Math.Abs(vals[k + 2] - vals[k - 2]);
                    if (g > bestTopGrad) { bestTopGrad = g; bestTopK = k; }
                }

                int bestBotK = -1;
                float bestBotGrad = 0;
                for (int k = scanYLen - 55; k < scanYLen - 5; k++)
                {
                    float g = Math.Abs(vals[k + 2] - vals[k - 2]);
                    if (g > bestBotGrad) { bestBotGrad = g; bestBotK = k; }
                }

                int actualTopY = bestTopK >= 0 ? scanYStart + bestTopK : topY;
                int actualBotY = bestBotK >= 0 ? scanYStart + bestBotK : topY + targetH;

                Console.WriteLine($"Frame #{i+1}: hole=[{h.X}..{h.X+h.Width}], localFilm=[{localLeft}..{localRight}] -> localX={localFrameX}");
                Console.WriteLine($"   Y-analysis: holeCenterY={hCy}, baseTopY={topY} | topEdgeGrad={bestTopGrad:F1}(y={actualTopY}), botEdgeGrad={bestBotGrad:F1}(y={actualBotY}), span={actualBotY - actualTopY}");
            }

            var (straightMat, skew, frames) = detector.DetectAndStraighten(scanMat, format, dpi);
            Console.WriteLine($"\nDetectAndStraighten: Skew={skew:F2}°, MatSize={straightMat.Width}x{straightMat.Height}, Frames={frames.Count}");
            using var straightGray = new Mat();
            Cv2.CvtColor(straightMat, straightGray, ColorConversionCodes.BGR2GRAY);
            for (int i = 0; i < frames.Count; i++)
            {
                var r = frames[i];
                using var frameRoi = new Mat(straightGray, r);
                using var rightBorder = new Mat(frameRoi, new OpenCvSharp.Rect(r.Width - 4, 0, 4, r.Height));
                Scalar rightMean = Cv2.Mean(rightBorder);
                using var leftBorder = new Mat(frameRoi, new OpenCvSharp.Rect(0, 0, 4, r.Height));
                Scalar leftMean = Cv2.Mean(leftBorder);
                Console.WriteLine($"  Frame #{i+1}: X={r.X}, Y={r.Y}, W={r.Width}, H={r.Height}, Right={r.X + r.Width} | L_mean={leftMean.Val0:F1}, R_mean={rightMean.Val0:F1} {(rightMean.Val0 > 200 ? "[NG: 白ガラス混入]" : "[OK: フィルム内]")}");
            }
        }
    }
}
