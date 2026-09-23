using System.IO;
using System.IO.Compression;
using IrisPxS.Models;
using OpenCvSharp;

namespace IrisPxS.Services
{
    public class ExportOptions
    {
        public string OutputDirectory { get; set; } = string.Empty;
        public string OutputZipPath { get; set; } = string.Empty;
        public string Format { get; set; } = "JPEG"; // JPEG, TIFF, PNG
        public int JpegQuality { get; set; } = 95;
        public bool GenerateContactSheet { get; set; } = true;
        public string FileNameTemplate { get; set; } = "{RollName}_{Index:D2}";
    }

    public class RollExportService
    {
        private readonly FilmNegativeEngine _negativeEngine;
        private readonly DustScratchRemovalService _dustService;
        private readonly ExifMetadataService _exifService;

        public RollExportService(
            FilmNegativeEngine negativeEngine,
            DustScratchRemovalService dustService,
            ExifMetadataService exifService)
        {
            _negativeEngine = negativeEngine;
            _dustService = dustService;
            _exifService = exifService;
        }

        /// <summary>
        /// フィルム1本の全コマを指定フォルダに一括書き出し
        /// </summary>
        public async Task<List<string>> ExportToFolderAsync(
            RollSession session,
            ExportOptions options,
            IProgress<(string message, double progress)>? progress = null)
        {
            return await Task.Run(() =>
            {
                var exportedFiles = new List<string>();
                if (!Directory.Exists(options.OutputDirectory))
                {
                    Directory.CreateDirectory(options.OutputDirectory);
                }

                var allFrames = session.Strips.SelectMany(s => s.Frames).ToList();
                int total = allFrames.Count;
                if (total == 0) return exportedFiles;

                var processedFrameMats = new List<(FilmFrame frame, Mat mat)>();

                for (int i = 0; i < total; i++)
                {
                    var frame = allFrames[i];
                    double pct = (double)i / total * 0.9;
                    progress?.Report(($"コマ #{frame.FrameNumber:D2} 処理中 (NP変換・Exif付与)...", pct));

                    // 生画像の読み込み
                    if (string.IsNullOrEmpty(frame.RawImagePath) || !File.Exists(frame.RawImagePath))
                        continue;

                    using var rawMat = Cv2.ImRead(frame.RawImagePath, ImreadModes.Color);
                    if (rawMat.Empty()) continue;

                    // 赤外線ゴミ除去
                    Mat cleanMat;
                    if (frame.DustRemovalEnabled)
                    {
                        using var irMat = !string.IsNullOrEmpty(frame.IrImagePath) && File.Exists(frame.IrImagePath)
                            ? Cv2.ImRead(frame.IrImagePath, ImreadModes.Unchanged)
                            : null;

                        using var defectMask = irMat != null
                            ? _dustService.GenerateDefectMaskFromIr(irMat, frame.DustRemovalStrength)
                            : _dustService.GenerateDefectMaskFromColor(rawMat, frame.DustRemovalStrength);

                        cleanMat = _dustService.RemoveDustAndScratches(rawMat, defectMask);
                    }
                    else
                    {
                        cleanMat = rawMat.Clone();
                    }

                    // NP変換 (ネガポジ反転・プロファイル適用)
                    var positiveMat = _negativeEngine.ConvertNegativeToPositive(cleanMat, frame);
                    cleanMat.Dispose();

                    // 回転適用
                    if (Math.Abs(frame.RotationDegrees) > 0.1)
                    {
                        var rotated = ApplyRotation(positiveMat, frame.RotationDegrees);
                        positiveMat.Dispose();
                        positiveMat = rotated;
                    }

                    // ファイル名決定
                    string ext = options.Format.Equals("TIFF", StringComparison.OrdinalIgnoreCase) ? ".tif" :
                                 options.Format.Equals("PNG", StringComparison.OrdinalIgnoreCase) ? ".png" : ".jpg";

                    string fileName = options.FileNameTemplate
                        .Replace("{RollName}", session.RollName)
                        .Replace("{Index:D2}", $"{frame.FrameNumber:D2}")
                        .Replace("{Index}", $"{frame.FrameNumber}")
                        + ext;

                    string destPath = Path.Combine(options.OutputDirectory, fileName);

                    // Exif付与して保存
                    _exifService.SaveImageWithExif(positiveMat, frame, destPath, options.Format, options.JpegQuality);
                    exportedFiles.Add(destPath);

                    // コンタクトシート用キャッシュ
                    if (options.GenerateContactSheet)
                    {
                        // 縮小版を保持
                        var thumb = new Mat();
                        Cv2.Resize(positiveMat, thumb, new OpenCvSharp.Size(400, (int)(400 / 1.5)));
                        processedFrameMats.Add((frame, thumb));
                    }

                    positiveMat.Dispose();
                }

                // コンタクトシート（インデックスシート）生成
                if (options.GenerateContactSheet && processedFrameMats.Count > 0)
                {
                    progress?.Report(("コンタクトシート (インデックス画像) を生成中...", 0.95));
                    string contactSheetPath = Path.Combine(options.OutputDirectory, $"{session.RollName}_ContactSheet.jpg");
                    GenerateContactSheet(session, processedFrameMats, contactSheetPath);
                    exportedFiles.Add(contactSheetPath);

                    foreach (var p in processedFrameMats) p.mat.Dispose();
                }

                // メタデータサマリーテキスト出力
                string summaryPath = Path.Combine(options.OutputDirectory, $"{session.RollName}_Summary.txt");
                GenerateSummaryText(session, allFrames, summaryPath);
                exportedFiles.Add(summaryPath);

                progress?.Report(("一括書き出し完了！", 1.0));
                return exportedFiles;
            });
        }

        /// <summary>
        /// フィルム1本を丸ごとZIPアーカイブとして一括書き出し
        /// </summary>
        public async Task<string> ExportToZipAsync(
            RollSession session,
            ExportOptions options,
            IProgress<(string message, double progress)>? progress = null)
        {
            // 一旦一時フォルダに書き出し、それをZIPに圧縮
            string tempExportDir = Path.Combine(Path.GetTempPath(), $"IrisPxS_Zip_{Guid.NewGuid():N}");
            try
            {
                options.OutputDirectory = tempExportDir;
                await ExportToFolderAsync(session, options, progress);

                progress?.Report(("ZIPアーカイブに圧縮中...", 0.97));
                if (File.Exists(options.OutputZipPath))
                {
                    File.Delete(options.OutputZipPath);
                }

                var zipDir = Path.GetDirectoryName(options.OutputZipPath);
                if (!string.IsNullOrEmpty(zipDir) && !Directory.Exists(zipDir))
                {
                    Directory.CreateDirectory(zipDir);
                }

                ZipFile.CreateFromDirectory(tempExportDir, options.OutputZipPath, CompressionLevel.Optimal, false);
                progress?.Report(("ZIP書き出し完了！", 1.0));
                return options.OutputZipPath;
            }
            finally
            {
                if (Directory.Exists(tempExportDir))
                {
                    try { Directory.Delete(tempExportDir, true); } catch { }
                }
            }
        }

        private Mat ApplyRotation(Mat src, double degrees)
        {
            int code = -1;
            int deg = ((int)Math.Round(degrees) % 360 + 360) % 360;

            if (deg == 90) code = (int)RotateFlags.Rotate90Clockwise;
            else if (deg == 180) code = (int)RotateFlags.Rotate180;
            else if (deg == 270) code = (int)RotateFlags.Rotate90Counterclockwise;

            if (code >= 0)
            {
                var dst = new Mat();
                Cv2.Rotate(src, dst, (RotateFlags)code);
                return dst;
            }

            return src.Clone();
        }

        public Mat GenerateContactSheetMat(RollSession session)
        {
            var allFrames = session.Strips.SelectMany(s => s.Frames).ToList();
            var thumbs = new List<(FilmFrame frame, Mat mat)>();
            foreach (var frame in allFrames)
            {
                if (File.Exists(frame.RawImagePath))
                {
                    using var raw = Cv2.ImRead(frame.RawImagePath, ImreadModes.Color);
                    var r = frame.CropRect;
                    if (r.Width > 0 && r.Height > 0 && r.X >= 0 && r.Y >= 0 && r.X + r.Width <= raw.Width && r.Y + r.Height <= raw.Height)
                    {
                        using var cropped = new Mat(raw, r);
                        var inv = _negativeEngine.ConvertNegativeToPositive(cropped, frame);
                        var rot = ApplyRotation(inv, frame.RotationDegrees);
                        inv.Dispose();
                        thumbs.Add((frame, rot));
                    }
                }
            }

            int cols = Math.Max(1, Math.Min(6, thumbs.Count));
            int rows = (int)Math.Ceiling((double)thumbs.Count / (cols > 0 ? cols : 1));
            int thumbW = 400;
            int thumbH = 267;
            int padding = 20;
            int headerH = 120;
            int labelH = 40;

            int totalW = padding + cols * (thumbW + padding);
            int totalH = headerH + padding + Math.Max(1, rows) * (thumbH + labelH + padding);

            var sheet = new Mat(new OpenCvSharp.Size(totalW, totalH), MatType.CV_8UC3, new Scalar(25, 25, 25));

            Cv2.PutText(sheet, $"IRIS PxS - CONTACT SHEET: {session.RollName}",
                new OpenCvSharp.Point(padding, 45), HersheyFonts.HersheyComplex, 1.1, new Scalar(240, 240, 240), 2);

            string subTitle = $"Film: {session.FilmStock} | Camera: {session.DefaultCamera} | Lens: {session.DefaultLens} | Total: {thumbs.Count} Frames | Scanned: {DateTime.Now:yyyy/MM/dd}";
            Cv2.PutText(sheet, subTitle,
                new OpenCvSharp.Point(padding, 85), HersheyFonts.HersheyPlain, 1.2, new Scalar(180, 180, 180), 1);

            for (int i = 0; i < thumbs.Count; i++)
            {
                int r = i / cols;
                int c = i % cols;
                int x = padding + c * (thumbW + padding);
                int y = headerH + padding + r * (thumbH + labelH + padding);

                var (frame, thumb) = thumbs[i];
                var roi = sheet[new OpenCvSharp.Rect(x, y, thumbW, thumbH)];
                using var resized = new Mat();
                Cv2.Resize(thumb, resized, new OpenCvSharp.Size(thumbW, thumbH));
                resized.CopyTo(roi);
                thumb.Dispose();

                Cv2.Rectangle(sheet, new OpenCvSharp.Rect(x, y, thumbW, thumbH), new Scalar(80, 80, 80), 1);
                string label = $"#{frame.FrameNumber:D2}  f/{frame.FNumber:0.#}  {frame.ShutterSpeed}s  ISO{frame.ISO}";
                Cv2.PutText(sheet, label,
                    new OpenCvSharp.Point(x + 5, y + thumbH + 24), HersheyFonts.HersheyPlain, 1.1, new Scalar(220, 220, 220), 1);
            }

            return sheet;
        }

        private void GenerateContactSheet(RollSession session, List<(FilmFrame frame, Mat mat)> thumbs, string outputPath)
        {
            // 6列 × N行 のインデックスシート
            int cols = 6;
            int rows = (int)Math.Ceiling((double)thumbs.Count / cols);

            int thumbW = 400;
            int thumbH = 267;
            int padding = 20;
            int headerH = 120;
            int labelH = 40;

            int totalW = padding + cols * (thumbW + padding);
            int totalH = headerH + padding + rows * (thumbH + labelH + padding);

            using var sheet = new Mat(new OpenCvSharp.Size(totalW, totalH), MatType.CV_8UC3, new Scalar(25, 25, 25));

            // ヘッダー情報描画
            Cv2.PutText(sheet, $"IRIS PxS - CONTACT SHEET: {session.RollName}",
                new OpenCvSharp.Point(padding, 45), HersheyFonts.HersheyComplex, 1.1, new Scalar(240, 240, 240), 2);

            string subTitle = $"Film: {session.FilmStock} | Camera: {session.DefaultCamera} | Lens: {session.DefaultLens} | Total: {thumbs.Count} Frames | Scanned: {DateTime.Now:yyyy/MM/dd}";
            Cv2.PutText(sheet, subTitle,
                new OpenCvSharp.Point(padding, 85), HersheyFonts.HersheyPlain, 1.2, new Scalar(180, 180, 180), 1);

            // 各コマの配置
            for (int i = 0; i < thumbs.Count; i++)
            {
                int r = i / cols;
                int c = i % cols;

                int x = padding + c * (thumbW + padding);
                int y = headerH + padding + r * (thumbH + labelH + padding);

                var (frame, thumb) = thumbs[i];

                // コマ画像貼り付け
                var roi = sheet[new OpenCvSharp.Rect(x, y, thumbW, thumbH)];
                using var resized = new Mat();
                Cv2.Resize(thumb, resized, new OpenCvSharp.Size(thumbW, thumbH));
                resized.CopyTo(roi);

                // コマ枠線
                Cv2.Rectangle(sheet, new OpenCvSharp.Rect(x, y, thumbW, thumbH), new Scalar(80, 80, 80), 1);

                // キャプション・Exifラベル描画
                string label = $"#{frame.FrameNumber:D2}  f/{frame.FNumber:0.#}  {frame.ShutterSpeed}s  ISO{frame.ISO}";
                Cv2.PutText(sheet, label,
                    new OpenCvSharp.Point(x + 5, y + thumbH + 24), HersheyFonts.HersheyPlain, 1.1, new Scalar(220, 220, 220), 1);
            }

            Cv2.ImWrite(outputPath, sheet);
        }

        private void GenerateSummaryText(RollSession session, List<FilmFrame> frames, string outputPath)
        {
            using var writer = new StreamWriter(outputPath);
            writer.WriteLine("================================================================================");
            writer.WriteLine($" IRIS PxS Film Scan Summary: {session.RollName}");
            writer.WriteLine("================================================================================");
            writer.WriteLine($"Film Stock : {session.FilmStock}");
            writer.WriteLine($"Camera     : {session.DefaultCamera}");
            writer.WriteLine($"DefaultLens: {session.DefaultLens}");
            writer.WriteLine($"Total Frames: {frames.Count}");
            writer.WriteLine($"Export Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            writer.WriteLine("--------------------------------------------------------------------------------");
            writer.WriteLine("Frame | Shutter | F-Number | Focal | ISO | Base Color | Notes");
            writer.WriteLine("--------------------------------------------------------------------------------");

            foreach (var f in frames)
            {
                writer.WriteLine($"#{f.FrameNumber:D2}    | {f.ShutterSpeed,-7} | f/{f.FNumber,-6:0.#} | {f.FocalLength}mm | {f.ISO,-4} | #{f.BaseColorR:X2}{f.BaseColorG:X2}{f.BaseColorB:X2} | {f.Notes}");
            }
            writer.WriteLine("================================================================================");
        }
    }
}
