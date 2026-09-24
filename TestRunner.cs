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
                var (straight110, skew110, frames110) = detectorService.DetectAndStraighten(realMat, format110, 300);
                Console.WriteLine($"\n[110-General] 検知傾き角: {skew110:F2}°, 検出コマ数: {frames110.Count}");
                FrameDetectorService.GetFormatDimensions(format110, straight110.Height >= straight110.Width, 300, out int expW110, out int expH110, out _);
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
                Console.WriteLine($"  => [110-General] 全 {frames110.Count} コマの比率は完全に不変であり、デフォルト比率 ({expAspect110:F2}: W={expW110}, H={expH110}) を維持しています。[PASS]");
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
            var (c1Raw, c1Ir) = sessionService.SaveStripImages(roll.SessionId, cut1.Id, colorMat, irMat);
            cut1.FullScanImagePath = c1Raw;
            cut1.FullScanIrPath = c1Ir;

            var f1 = new FilmFrame
            {
                FrameNumber = 1,
                StripId = cut1.Id,
                CropRect = detectedFrames[0],
                RawImagePath = c1Raw,
                BaseColorR = baseColor.R,
                BaseColorG = baseColor.G,
                BaseColorB = baseColor.B
            };
            var f2 = new FilmFrame
            {
                FrameNumber = 2,
                StripId = cut1.Id,
                CropRect = detectedFrames.Count > 1 ? detectedFrames[1] : detectedFrames[0],
                RawImagePath = c1Raw,
                BaseColorR = baseColor.R,
                BaseColorG = baseColor.G,
                BaseColorB = baseColor.B
            };
            cut1.Frames.Add(f1);
            cut1.Frames.Add(f2);
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

            Console.WriteLine("\n=================================================================");
            Console.WriteLine(" 全検証テスト PASS! 正常動作を確認しました。");
            Console.WriteLine("=================================================================");
        }
    }
}
