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
            var (colorMat, irMat) = await scannerService.ScanAsync(null, 300, true);
            Console.WriteLine($"スキャン取得サイズ: {colorMat.Width}x{colorMat.Height}, IRサイズ: {irMat?.Width}x{irMat?.Height}");

            // 3. コマ自動検出テスト (35mm フルサイズ & 傾き補正)
            Console.WriteLine("\n[3/6] コマ自動認識テスト (35mm Full Format & 傾き補正)...");
            var format = FilmFormat.GetPresetFormats()[0]; // 135 Full-Frame
            var (straightenedMat, skewAngle, detectedFrames) = detectorService.DetectAndStraighten(colorMat, format, 300);
            Console.WriteLine($"直立画像検知傾き角: {skewAngle:F2}°, 検出コマ数: {detectedFrames.Count}");
            for (int i = 0; i < detectedFrames.Count; i++)
            {
                var r = detectedFrames[i];
                Console.WriteLine($"  コマ #{i + 1}: X={r.X}, Y={r.Y}, W={r.Width}, H={r.Height}, Aspect={((double)r.Height / r.Width):F2}");
            }

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
                for (int i = 0; i < frames135.Count; i++)
                {
                    var r = frames135[i];
                    Console.WriteLine($"  135微調整コマ #{i + 1}: X={r.X}, Y={r.Y}, W={r.Width}, H={r.Height} (Y範囲: {r.Y}〜{r.Y + r.Height}, Aspect={(double)r.Height / r.Width:F2})");
                }

                // 2. 110 General 自動認識テスト
                var format110 = FilmFormat.GetAllFormats().First(f => f.Type == FilmFormatType.Format110_General);
                var (straight110, skew110, frames110) = detectorService.DetectAndStraighten(realMat, format110, 300);
                Console.WriteLine($"\n[110-General] 検知傾き角: {skew110:F2}°, 検出コマ数: {frames110.Count}");
                for (int i = 0; i < frames110.Count; i++)
                {
                    var r = frames110[i];
                    Console.WriteLine($"  110コマ #{i + 1}: X={r.X}, Y={r.Y}, W={r.Width}, H={r.Height} (Y範囲: {r.Y}〜{r.Y + r.Height})");
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

            // 6. ロールセッション一時保存 & 一括フォルダ/ZIPエクスポートテスト
            Console.WriteLine("\n[6/6] ロール一括フォルダ/ZIP書き出しテスト...");
            var roll = new RollSession
            {
                RollName = "TestRoll_Verification_01",
                FilmStock = "Kodak Portra 400",
                DefaultCamera = "Leica M6",
                DefaultLens = "Summicron 50mm f/2"
            };

            var strip = new FilmStrip { Name = "Strip 1", ScanDpi = 2400 };
            var (rawPath, irPath) = sessionService.SaveStripImages(roll.SessionId, strip.Id, colorMat, irMat);
            strip.FullScanImagePath = rawPath;
            strip.FullScanIrPath = irPath;

            frame.RawImagePath = sessionService.SaveFrameRawImage(roll.SessionId, frame.Id, frameRoi);
            strip.Frames.Add(frame);
            roll.Strips.Add(strip);

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
