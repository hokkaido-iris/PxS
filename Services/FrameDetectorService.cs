using OpenCvSharp;
using IrisPxS.Models;

namespace IrisPxS.Services
{
    public class FrameDetectorService
    {
        /// <summary>
        /// フィルムとメディアがない部分（透過光素抜けガラス）のコントラスト境界から斜めの傾き角（Skew Angle）を検出し、
        /// スキャン画像を自動正立（De-skew）した上で、選択フォーマットの物理寸法・縦横比に厳密に基づいたコマ枠を生成する
        /// </summary>
        public (Mat straightenedMat, double skewAngle, List<OpenCvSharp.Rect> frames) DetectAndStraighten(
            Mat scanMat,
            FilmFormat format,
            int dpi = 0)
        {
            if (scanMat == null || scanMat.Empty())
            {
                return (new Mat(), 0.0, new List<OpenCvSharp.Rect>());
            }

            // DPIの自動算出（指定がない場合、スキャン画像の長辺とGT-X820透過原稿エリアから推定）
            if (dpi <= 0)
            {
                int maxDim = Math.Max(scanMat.Width, scanMat.Height);
                dpi = (int)Math.Round(maxDim / (240.0 / 25.4));
                if (dpi < 150) dpi = 300;
            }

            // 1. フィルムとメディアがない部分（素抜けガラス）のコントラスト境界から傾き角（度）を精密検出
            double skewAngle = DetectFilmSkewAngleFromMediaBoundary(scanMat);

            // 2. 傾きがある場合（絶対値 0.1度以上）、画像を自動正立（回転補正）
            Mat workingMat;
            if (Math.Abs(skewAngle) >= 0.1)
            {
                workingMat = StraightenImage(scanMat, skewAngle);
            }
            else
            {
                workingMat = scanMat.Clone();
            }

            // 3. 正立された画像から、フォーマットの物理サイズと縦横比に基づきコマ枠を検出・配置
            var frames = DetectFramesOnStraightened(workingMat, format, dpi);

            return (workingMat, skewAngle, frames);
        }

        /// <summary>
        /// フィルムとメディアがない部分（透過光の素抜け部・白色飽和領域）のコントラスト境界から
        /// フィルムストリップの物理的な傾き角度（-45度〜+45度対応）を精密に検出する
        /// </summary>
        public double DetectFilmSkewAngleFromMediaBoundary(Mat scanMat)
        {
            try
            {
                // 高速かつ大域的な解析のため、最大長辺 800px に縮小
                double maxDim = Math.Max(scanMat.Width, scanMat.Height);
                double scale = 800.0 / maxDim;
                int smallW = Math.Max(10, (int)(scanMat.Width * scale));
                int smallH = Math.Max(10, (int)(scanMat.Height * scale));

                using var small = new Mat();
                Cv2.Resize(scanMat, small, new OpenCvSharp.Size(smallW, smallH), 0, 0, InterpolationFlags.Area);

                using var gray = new Mat();
                Cv2.CvtColor(small, gray, ColorConversionCodes.BGR2GRAY);

                // --- 1. メディアがない部分（透過光素抜けガラス）とフィルム領域の分離 ---
                // スキャナーの透過原稿ユニットでは、フィルムが存在しない部分は光源が直通するため
                // 輝度が極めて高く飽和に近い状態（白色）になります。
                // 一方フィルムが存在する部分は未露光ベースであっても光を吸収するため明らかに暗くなります。
                Cv2.MinMaxLoc(gray, out double minVal, out double maxVal);

                // 素抜け領域の閾値（画像内最大輝度の85%〜90%、最低180）
                double glassThreshold = Math.Max(180.0, maxVal * 0.88);
                if (glassThreshold > 245.0) glassThreshold = 235.0;

                // フィルム領域マスク (30 <= Y <= glassThreshold)
                using var filmMask = new Mat();
                Cv2.InRange(gray, new Scalar(30), new Scalar(glassThreshold), filmMask);

                // パーフォレーション穴や未露光の抜けを埋めるモルフォロジー結合
                int kSize = Math.Max(15, (int)(smallW * 0.04));
                if (kSize % 2 == 0) kSize++;
                using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(kSize, kSize));
                Cv2.MorphologyEx(filmMask, filmMask, MorphTypes.Close, kernel);

                // --- 2. フィルム外郭輪郭の検出と最小外接回転矩形 (MinAreaRect) ---
                Cv2.FindContours(filmMask, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

                double candidateAngle = 0.0;

                if (contours.Length > 0)
                {
                    // 最も面積の大きい輪郭（フィルムストリップ全体）
                    var filmContours = contours
                        .Where(c => Cv2.ContourArea(c) > (smallW * smallH * 0.03))
                        .OrderByDescending(c => Cv2.ContourArea(c))
                        .ToList();

                    if (filmContours.Count > 0)
                    {
                        var maxContour = filmContours[0];
                        var rotRect = Cv2.MinAreaRect(maxContour);
                        var pts = rotRect.Points();

                        // フィルムの長辺ベクトルを計算（フィルムは短辺に対して2倍〜8倍の長さを持つ）
                        double dist01 = Math.Sqrt(Math.Pow(pts[1].X - pts[0].X, 2) + Math.Pow(pts[1].Y - pts[0].Y, 2));
                        double dist12 = Math.Sqrt(Math.Pow(pts[2].X - pts[1].X, 2) + Math.Pow(pts[2].Y - pts[1].Y, 2));

                        double dx, dy;
                        if (dist01 >= dist12)
                        {
                            dx = pts[1].X - pts[0].X;
                            dy = pts[1].Y - pts[0].Y;
                        }
                        else
                        {
                            dx = pts[2].X - pts[1].X;
                            dy = pts[2].Y - pts[1].Y;
                        }

                        // 縦ストリップ（Y軸方向が長手）か横ストリップかの判定
                        bool isVertical = Math.Abs(dy) >= Math.Abs(dx);

                        if (isVertical)
                        {
                            // 常に下向き (dy > 0) のベクトルに正規化
                            if (dy < 0) { dx = -dx; dy = -dy; }

                            // 垂直軸 (dx = 0, dy > 0) からの傾き角（度）
                            // 時計回り（右傾き）: dx > 0 => angle > 0
                            // 反時計回り（左傾き）: dx < 0 => angle < 0
                            candidateAngle = Math.Atan2(dx, dy) * (180.0 / Math.PI);
                        }
                        else
                        {
                            // 常に右向き (dx > 0) のベクトルに正規化
                            if (dx < 0) { dx = -dx; dy = -dy; }

                            // 水平軸 (dx > 0, dy = 0) からの傾き角（度）
                            candidateAngle = Math.Atan2(dy, dx) * (180.0 / Math.PI);
                        }
                    }
                }

                return Math.Round(candidateAngle, 2);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DetectFilmSkewAngleFromMediaBoundary error: {ex.Message}");
                return 0.0;
            }
        }

        /// <summary>
        /// 画像の投影プロファイルのコントラスト（分散）を最大化する精密角度探索
        /// </summary>
        private double RefineAngleByProjectionContrast(Mat gray, bool isVertical, double candidateAngle)
        {
            double bestAngle = candidateAngle;
            double maxContrastScore = -1.0;

            double searchStart = Math.Max(-45.0, candidateAngle - 2.0);
            double searchEnd = Math.Min(45.0, candidateAngle + 2.0);

            for (double angle = searchStart; angle <= searchEnd; angle += 0.05)
            {
                using var rotMat = Cv2.GetRotationMatrix2D(new Point2f(gray.Width / 2f, gray.Height / 2f), -angle, 1.0);
                using var rotated = new Mat();
                Cv2.WarpAffine(gray, rotated, rotMat, gray.Size(), InterpolationFlags.Linear, BorderTypes.Replicate);

                // 投影プロファイルの計算
                double contrastScore = 0;

                if (isVertical)
                {
                    using var colMean = new Mat();
                    Cv2.Reduce(rotated, colMean, ReduceDimension.Row, ReduceTypes.Avg, MatType.CV_32F);

                    int colLen = colMean.Width;
                    float[] colArr = new float[colLen];
                    System.Runtime.InteropServices.Marshal.Copy(colMean.Data, colArr, 0, colLen);

                    for (int x = 1; x < colLen; x++)
                    {
                        float diff = colArr[x] - colArr[x - 1];
                        contrastScore += diff * diff;
                    }
                }
                else
                {
                    using var rowMean = new Mat();
                    Cv2.Reduce(rotated, rowMean, ReduceDimension.Column, ReduceTypes.Avg, MatType.CV_32F);

                    int rowLen = rowMean.Height;
                    float[] rowArr = new float[rowLen];
                    System.Runtime.InteropServices.Marshal.Copy(rowMean.Data, rowArr, 0, rowLen);

                    for (int y = 1; y < rowLen; y++)
                    {
                        float diff = rowArr[y] - rowArr[y - 1];
                        contrastScore += diff * diff;
                    }
                }

                if (contrastScore > maxContrastScore)
                {
                    maxContrastScore = contrastScore;
                    bestAngle = angle;
                }
            }

            return Math.Round(bestAngle, 2);
        }

        /// <summary>
        /// スキャン画像を傾き角 skewAngle に応じて正立（回転補正）する
        /// </summary>
        public Mat StraightenImage(Mat src, double skewAngle)
        {
            if (Math.Abs(skewAngle) < 0.05) return src.Clone();

            var center = new Point2f(src.Width / 2.0f, src.Height / 2.0f);
            using var rotMat = Cv2.GetRotationMatrix2D(center, -skewAngle, 1.0);

            var dst = new Mat();
            Cv2.WarpAffine(src, dst, rotMat, src.Size(), InterpolationFlags.Cubic, BorderTypes.Replicate);
            return dst;
        }

        /// <summary>
        /// フォーマット仕様（mm）とスキャンDPIから厳密なコマサイズ・縦横比・ピッチを計算
        /// </summary>
        public static void GetFormatDimensions(
            FilmFormat format,
            bool isVerticalStrip,
            int dpi,
            out int targetW,
            out int targetH,
            out int pitchPx)
        {
            double mmToPx = (double)dpi / 25.4;

            double dimAcross;
            double dimAlong;
            double marginMm;

            switch (format.Type)
            {
                case FilmFormatType.Format135_Full:
                    dimAcross = 24.0;
                    dimAlong = 36.0;
                    marginMm = 2.0; // 38mm total pitch
                    break;
                case FilmFormatType.Format135_Half:
                    dimAcross = 24.0;
                    dimAlong = 18.0;
                    marginMm = 1.5;
                    break;
                case FilmFormatType.Format120_645:
                    dimAcross = 56.0;
                    dimAlong = 41.5;
                    marginMm = 4.0;
                    break;
                case FilmFormatType.Format120_66:
                    dimAcross = 56.0;
                    dimAlong = 56.0;
                    marginMm = 6.0;
                    break;
                case FilmFormatType.Format120_67:
                    dimAcross = 56.0;
                    dimAlong = 70.0;
                    marginMm = 6.0;
                    break;
                case FilmFormatType.Format120_69:
                    dimAcross = 56.0;
                    dimAlong = 84.0;
                    marginMm = 6.0;
                    break;
                case FilmFormatType.Format127_465:
                    dimAcross = 40.0;
                    dimAlong = 65.0;
                    marginMm = 3.0;
                    break;
                case FilmFormatType.Format127_44:
                    dimAcross = 40.0;
                    dimAlong = 40.0;
                    marginMm = 3.0;
                    break;
                case FilmFormatType.Format127_43:
                    dimAcross = 40.0;
                    dimAlong = 30.0;
                    marginMm = 3.0;
                    break;
                case FilmFormatType.Format110_General:
                    dimAcross = 13.0;
                    dimAlong = 17.0;
                    marginMm = 2.0;
                    break;
                default:
                    dimAcross = Math.Min(format.PhysicalWidthMm, format.PhysicalHeightMm);
                    dimAlong = Math.Max(format.PhysicalWidthMm, format.PhysicalHeightMm);
                    marginMm = 2.0;
                    break;
            }

            if (isVerticalStrip)
            {
                // 縦ストリップ: X軸＝フィルム幅方向(dimAcross), Y軸＝コマ送り方向(dimAlong)
                targetW = (int)Math.Round(dimAcross * mmToPx);
                targetH = (int)Math.Round(dimAlong * mmToPx);
                pitchPx = (int)Math.Round((dimAlong + marginMm) * mmToPx);
            }
            else
            {
                // 横ストリップ: X軸＝コマ送り方向(dimAlong), Y軸＝フィルム幅方向(dimAcross)
                targetW = (int)Math.Round(dimAlong * mmToPx);
                targetH = (int)Math.Round(dimAcross * mmToPx);
                pitchPx = (int)Math.Round((dimAlong + marginMm) * mmToPx);
            }
        }

        /// <summary>
        /// 正立された画像上でフィルムストリップ位置を特定し、フォーマット通りのコマ枠を確実に配置
        /// </summary>
        private List<OpenCvSharp.Rect> DetectFramesOnStraightened(Mat scanMat, FilmFormat format, int dpi)
        {
            var frames = new List<OpenCvSharp.Rect>();
            if (scanMat == null || scanMat.Empty()) return frames;

            bool isVertical = scanMat.Height >= scanMat.Width;

            // DPI 不整合（高DPI設定のままプレビュー画像に適用した場合など）の自動検出と是正
            // 物理サイズから算出した枠がスキャン画像全体よりも大きい場合は、画像実寸から実効DPIを再計算
            GetFormatDimensions(format, isVertical, dpi, out int targetW, out int targetH, out int pitchPx);

            if ((isVertical && targetH >= scanMat.Height) || (!isVertical && targetW >= scanMat.Width))
            {
                // 画像の実寸法からGT-X820透過エリア仕様（長辺約240mm）に基づき適正DPIを再算出
                int maxDim = Math.Max(scanMat.Width, scanMat.Height);
                dpi = (int)Math.Round((double)maxDim / (240.0 / 25.4));
                if (dpi < 100) dpi = 150;
                GetFormatDimensions(format, isVertical, dpi, out targetW, out targetH, out pitchPx);
            }

            // フィルム領域の特定
            var filmBounds = DetectFilmStripBounds(scanMat);

            int desiredCount = format.DefaultFramesPerStrip > 0 ? format.DefaultFramesPerStrip : 6;

            if (isVertical)
            {
                int frameW = Math.Min(targetW, scanMat.Width - 4);
                int frameH = targetH;

                // X方向（幅方向）の中央位置決定: 検出されたフィルム帯があればその中央、なければ画像全体の中央
                int startX;
                if (filmBounds.Width >= frameW && filmBounds.X >= 0)
                {
                    startX = filmBounds.X + (filmBounds.Width - frameW) / 2;
                }
                else
                {
                    startX = (scanMat.Width - frameW) / 2;
                }
                startX = Math.Max(0, Math.Min(startX, scanMat.Width - frameW));

                // Y方向（コマ送り方向）の開始位置とコマ数
                int marginPx = (int)Math.Round(3.0 * dpi / 25.4);
                int startY = filmBounds.Y >= 0 ? filmBounds.Y + marginPx : marginPx;

                // スキャン画像内に収まる最大コマ数
                int countToGenerate = desiredCount;
                if (startY + countToGenerate * pitchPx > scanMat.Height)
                {
                    // 上端から収まらない場合、中央揃えで収まるか試行
                    int totalSpan = (countToGenerate - 1) * pitchPx + frameH;
                    if (totalSpan <= scanMat.Height)
                    {
                        startY = (scanMat.Height - totalSpan) / 2;
                    }
                    else
                    {
                        // それでも収まらない場合は、入るだけのコマ数に調整
                        countToGenerate = Math.Max(1, (scanMat.Height - marginPx * 2) / pitchPx);
                        startY = marginPx;
                    }
                }

                for (int i = 0; i < countToGenerate; i++)
                {
                    int y = startY + i * pitchPx;
                    if (y + frameH > scanMat.Height)
                    {
                        y = Math.Max(0, scanMat.Height - frameH);
                    }

                    frames.Add(new OpenCvSharp.Rect(startX, y, frameW, frameH));
                }

                // フェイルセーフ: 万一0コマなら中央に強制配置
                if (frames.Count == 0)
                {
                    frames.Add(new OpenCvSharp.Rect(
                        Math.Max(0, (scanMat.Width - frameW) / 2),
                        Math.Max(0, (scanMat.Height - frameH) / 2),
                        frameW,
                        Math.Min(frameH, scanMat.Height)));
                }
            }
            else
            {
                int frameW = targetW;
                int frameH = Math.Min(targetH, scanMat.Height - 4);

                int startY;
                if (filmBounds.Height >= frameH && filmBounds.Y >= 0)
                {
                    startY = filmBounds.Y + (filmBounds.Height - frameH) / 2;
                }
                else
                {
                    startY = (scanMat.Height - frameH) / 2;
                }
                startY = Math.Max(0, Math.Min(startY, scanMat.Height - frameH));

                int marginPx = (int)Math.Round(3.0 * dpi / 25.4);
                int startX = filmBounds.X >= 0 ? filmBounds.X + marginPx : marginPx;

                int countToGenerate = desiredCount;
                if (startX + countToGenerate * pitchPx > scanMat.Width)
                {
                    int totalSpan = (countToGenerate - 1) * pitchPx + frameW;
                    if (totalSpan <= scanMat.Width)
                    {
                        startX = (scanMat.Width - totalSpan) / 2;
                    }
                    else
                    {
                        countToGenerate = Math.Max(1, (scanMat.Width - marginPx * 2) / pitchPx);
                        startX = marginPx;
                    }
                }

                for (int i = 0; i < countToGenerate; i++)
                {
                    int x = startX + i * pitchPx;
                    if (x + frameW > scanMat.Width)
                    {
                        x = Math.Max(0, scanMat.Width - frameW);
                    }

                    frames.Add(new OpenCvSharp.Rect(x, startY, frameW, frameH));
                }

                if (frames.Count == 0)
                {
                    frames.Add(new OpenCvSharp.Rect(
                        Math.Max(0, (scanMat.Width - frameW) / 2),
                        Math.Max(0, (scanMat.Height - frameH) / 2),
                        Math.Min(frameW, scanMat.Width),
                        frameH));
                }
            }

            return frames;
        }

        /// <summary>
        /// スキャナー透過原稿領域の中からフィルムストリップが存在する境界を検出
        /// </summary>
        private OpenCvSharp.Rect DetectFilmStripBounds(Mat scanMat)
        {
            try
            {
                double scale = 400.0 / Math.Max(scanMat.Width, scanMat.Height);
                using var small = new Mat();
                Cv2.Resize(scanMat, small, new OpenCvSharp.Size(scanMat.Width * scale, scanMat.Height * scale));

                using var gray = new Mat();
                Cv2.CvtColor(small, gray, ColorConversionCodes.BGR2GRAY);

                Cv2.MinMaxLoc(gray, out _, out double maxVal);
                double glassThreshold = Math.Max(180.0, maxVal * 0.88);
                if (glassThreshold > 245.0) glassThreshold = 235.0;

                using var mask = new Mat();
                Cv2.InRange(gray, new Scalar(30), new Scalar(glassThreshold), mask);

                using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(15, 15));
                Cv2.MorphologyEx(mask, mask, MorphTypes.Close, kernel);

                Cv2.FindContours(mask, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

                if (contours.Length > 0)
                {
                    var maxContour = contours.OrderByDescending(c => Cv2.ContourArea(c)).First();
                    var r = Cv2.BoundingRect(maxContour);

                    if (r.Width > 20 && r.Height > 20)
                    {
                        int origX = Math.Max(0, (int)(r.X / scale));
                        int origY = Math.Max(0, (int)(r.Y / scale));
                        int origW = Math.Min(scanMat.Width - origX, (int)(r.Width / scale));
                        int origH = Math.Min(scanMat.Height - origY, (int)(r.Height / scale));

                        return new OpenCvSharp.Rect(origX, origY, origW, origH);
                    }
                }
            }
            catch { }

            // 検出できなかった場合のセーフデフォルト（中央領域）
            int defW = (int)(scanMat.Width * 0.85);
            int defH = (int)(scanMat.Height * 0.90);
            return new OpenCvSharp.Rect(
                (scanMat.Width - defW) / 2,
                (scanMat.Height - defH) / 2,
                defW,
                defH
            );
        }

        /// <summary>
        /// 既存シグネチャ互換用
        /// </summary>
        public List<OpenCvSharp.Rect> DetectFrames(Mat scanMat, FilmFormat format, int dpi = 0)
        {
            var (_, _, frames) = DetectAndStraighten(scanMat, format, dpi);
            return frames;
        }

        /// <summary>
        /// 既存シグネチャ互換用
        /// </summary>
        public double DetectFilmSkewAngle(Mat scanMat)
        {
            return DetectFilmSkewAngleFromMediaBoundary(scanMat);
        }
    }
}
