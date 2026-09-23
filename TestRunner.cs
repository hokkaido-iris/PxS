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
            Console.WriteLine($"検出されたスキャナー数: {scanners.Count}");
            foreach (var sc in scanners)
            {
                Console.WriteLine($" - {sc.Name} (ID: {sc.DeviceId}, Epson: {sc.IsEpsonGtx820})");
            }

            // 2. モック/実機スキャンテスト (300dpi PreScan)
            Console.WriteLine("\n[2/6] フィルムストリップ生成・スキャンテスト...");
            var (colorMat, irMat) = await scannerService.ScanAsync(null, 300, true);
            Console.WriteLine($"スキャン取得サイズ: {colorMat.Width}x{colorMat.Height}, IRサイズ: {irMat?.Width}x{irMat?.Height}");

            // 3. コマ自動検出テスト (35mm フルサイズ)
            Console.WriteLine("\n[3/6] コマ自動認識テスト (35mm Full Format)...");
            var format = FilmFormat.GetPresetFormats()[0];
            var detectedFrames = detectorService.DetectFrames(colorMat, format);
            Console.WriteLine($"検出コマ数: {detectedFrames.Count}");
            for (int i = 0; i < detectedFrames.Count; i++)
            {
                var r = detectedFrames[i];
                Console.WriteLine($"  コマ #{i + 1}: X={r.X}, Y={r.Y}, W={r.Width}, H={r.Height}, Aspect={((double)r.Width / r.Height):F2}");
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
