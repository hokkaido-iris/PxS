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
        /// 回転ではみ出た領域も一切クリップせず、全領域を包含する新しいサイズへ拡張
        /// </summary>
        public Mat StraightenImage(Mat src, double skewAngle)
        {
            if (Math.Abs(skewAngle) < 0.05) return src.Clone();

            double rad = Math.Abs(skewAngle) * Math.PI / 180.0;
            double sin = Math.Sin(rad);
            double cos = Math.Cos(rad);

            int newW = (int)Math.Ceiling(src.Width * cos + src.Height * sin);
            int newH = (int)Math.Ceiling(src.Width * sin + src.Height * cos);

            var center = new Point2f(src.Width / 2.0f, src.Height / 2.0f);
            using var rotMat = Cv2.GetRotationMatrix2D(center, -skewAngle, 1.0);

            // 拡張キャンバスの中央へ平行移動オフセットを加算
            rotMat.Set(0, 2, rotMat.At<double>(0, 2) + (newW - src.Width) / 2.0);
            rotMat.Set(1, 2, rotMat.At<double>(1, 2) + (newH - src.Height) / 2.0);

            var dst = new Mat();
            // 余白は透過スキャナーの素抜けガラス色（白色: 255, 255, 255）でパディング
            Cv2.WarpAffine(src, dst, rotMat, new OpenCvSharp.Size(newW, newH), InterpolationFlags.Cubic, BorderTypes.Constant, new Scalar(255, 255, 255));
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
        /// 正立された画像上でフィルムストリップ位置を特定し、写真内容認識型でコマ枠を精密配置
        /// </summary>
        private List<OpenCvSharp.Rect> DetectFramesOnStraightened(Mat scanMat, FilmFormat format, int dpi)
        {
            var frames = new List<OpenCvSharp.Rect>();
            if (scanMat == null || scanMat.Empty()) return frames;

            bool isVertical = scanMat.Height >= scanMat.Width;

            // DPI 不整合（高DPI設定のままプレビュー画像に適用した場合など）の自動検出と是正
            GetFormatDimensions(format, isVertical, dpi, out int targetW, out int targetH, out int pitchPx);

            if ((isVertical && targetH >= scanMat.Height) || (!isVertical && targetW >= scanMat.Width))
            {
                int maxDim = Math.Max(scanMat.Width, scanMat.Height);
                dpi = (int)Math.Round((double)maxDim / (240.0 / 25.4));
                if (dpi < 100) dpi = 150;
                GetFormatDimensions(format, isVertical, dpi, out targetW, out targetH, out pitchPx);
            }

            int desiredCount = format.DefaultFramesPerStrip > 0 ? format.DefaultFramesPerStrip : 6;

            if (isVertical)
            {
                int frameW = Math.Min(targetW, scanMat.Width - 4);
                int frameH = targetH;

                // 1. フィルムストリップの正確な左右境界 (X_L, X_R) の検出
                var (filmLeft, filmRight) = DetectFilmHorizontalEdges(scanMat);

                int startX;
                if (filmRight > filmLeft && (filmRight - filmLeft) >= frameW)
                {
                    // フィルム帯中央に24mm写真トラックを配置
                    startX = filmLeft + ((filmRight - filmLeft) - frameW) / 2;
                }
                else
                {
                    startX = (scanMat.Width - frameW) / 2;
                }
                startX = Math.Max(0, Math.Min(startX, scanMat.Width - frameW));

                // 2. 中央写真トラックからプロファイルと露光リーダー部を抽出
                int trackX = startX + (int)(frameW * 0.10);
                int trackW = Math.Max(10, (int)(frameW * 0.80));
                var trackZone = new OpenCvSharp.Rect(trackX, 0, trackW, scanMat.Height);

                float[] profile = ComputeActivityProfile(scanMat, true, trackZone);
                int leadEnd = DetectDarkLeaderEnd(scanMat, trackZone);

                // 3. 写真トラック内で最もコントラスト・ディテールの高い「アンカーコマ」を検出
                int snapRadius = Math.Max(2, (int)Math.Round(2.5 * dpi / 25.4));
                int anchorY = FindAnchorFrameStartY(profile, frameH, pitchPx, leadEnd, scanMat.Height);

                // 4. アンカーコマを基準に、フィルム機械規格ピッチ（38.0mm周期）で前後に同期展開
                var yPositions = GenerateSynchronizedYPositions(anchorY, frameH, pitchPx, leadEnd, scanMat.Height, desiredCount, profile, snapRadius);

                foreach (int y in yPositions)
                {
                    int clampedY = Math.Max(0, Math.Min(y, scanMat.Height - frameH));
                    frames.Add(new OpenCvSharp.Rect(startX, clampedY, frameW, frameH));
                }

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
                // 横ストリップ
                int frameW = targetW;
                int frameH = Math.Min(targetH, scanMat.Height - 4);

                var (filmTop, filmBottom) = DetectFilmVerticalEdges(scanMat);

                int startY;
                if (filmBottom > filmTop && (filmBottom - filmTop) >= frameH)
                {
                    startY = filmTop + ((filmBottom - filmTop) - frameH) / 2;
                }
                else
                {
                    startY = (scanMat.Height - frameH) / 2;
                }
                startY = Math.Max(0, Math.Min(startY, scanMat.Height - frameH));

                int trackY = startY + (int)(frameH * 0.10);
                int trackH = Math.Max(10, (int)(frameH * 0.80));
                var trackZone = new OpenCvSharp.Rect(0, trackY, scanMat.Width, trackH);

                float[] profile = ComputeActivityProfile(scanMat, false, trackZone);
                int leadEnd = DetectDarkLeaderEnd(scanMat, trackZone, isVertical: false);

                int snapRadius = Math.Max(2, (int)Math.Round(2.5 * dpi / 25.4));
                int anchorX = FindAnchorFrameStartY(profile, frameW, pitchPx, leadEnd, scanMat.Width);

                var xPositions = GenerateSynchronizedYPositions(anchorX, frameW, pitchPx, leadEnd, scanMat.Width, desiredCount, profile, snapRadius);

                foreach (int x in xPositions)
                {
                    int clampedX = Math.Max(0, Math.Min(x, scanMat.Width - frameW));
                    frames.Add(new OpenCvSharp.Rect(clampedX, startY, frameW, frameH));
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
        /// 透過スキャン画像からフィルムストリップの正確な左右境界 (X_L, X_R) を検出
        /// </summary>
        private (int Left, int Right) DetectFilmHorizontalEdges(Mat scanMat)
        {
            try
            {
                using var gray = new Mat();
                Cv2.CvtColor(scanMat, gray, ColorConversionCodes.BGR2GRAY);

                // 中央付近の高さ50%領域で水平プロファイルを計算（上下端のホルダー遮光板などを回避）
                int midY = (int)(scanMat.Height * 0.25);
                int midH = (int)(scanMat.Height * 0.50);
                using var roi = new Mat(gray, new OpenCvSharp.Rect(0, midY, scanMat.Width, midH));

                using var colMean = new Mat();
                Cv2.Reduce(roi, colMean, ReduceDimension.Row, ReduceTypes.Avg, MatType.CV_32F);

                float[] cols = new float[scanMat.Width];
                System.Runtime.InteropServices.Marshal.Copy(colMean.Data, cols, 0, scanMat.Width);

                // ガラス面（素抜け）の輝度基準値（通常 240 以上）
                float maxVal = cols.Max();
                float glassThresh = Math.Max(210f, maxVal * 0.90f);

                int left = -1;
                int right = -1;

                // 左端探索: ガラス面からフィルム（暗い部分）への急変点
                for (int x = 5; x < scanMat.Width / 2; x++)
                {
                    if (cols[x] < glassThresh)
                    {
                        left = x;
                        break;
                    }
                }

                // 右端探索
                for (int x = scanMat.Width - 6; x > scanMat.Width / 2; x--)
                {
                    if (cols[x] < glassThresh)
                    {
                        right = x;
                        break;
                    }
                }

                if (left >= 0 && right > left && (right - left) > 100)
                {
                    return (left, right);
                }
            }
            catch { }

            // フォールバック
            return ((int)(scanMat.Width * 0.15), (int)(scanMat.Width * 0.85));
        }

        /// <summary>
        /// 横ストリップ時の正確な上下境界 (Y_Top, Y_Bottom) を検出
        /// </summary>
        private (int Top, int Bottom) DetectFilmVerticalEdges(Mat scanMat)
        {
            try
            {
                using var gray = new Mat();
                Cv2.CvtColor(scanMat, gray, ColorConversionCodes.BGR2GRAY);

                int midX = (int)(scanMat.Width * 0.25);
                int midW = (int)(scanMat.Width * 0.50);
                using var roi = new Mat(gray, new OpenCvSharp.Rect(midX, 0, midW, scanMat.Height));

                using var rowMean = new Mat();
                Cv2.Reduce(roi, rowMean, ReduceDimension.Column, ReduceTypes.Avg, MatType.CV_32F);

                float[] rows = new float[scanMat.Height];
                System.Runtime.InteropServices.Marshal.Copy(rowMean.Data, rows, 0, scanMat.Height);

                float maxVal = rows.Max();
                float glassThresh = Math.Max(210f, maxVal * 0.90f);

                int top = -1;
                int bottom = -1;

                for (int y = 5; y < scanMat.Height / 2; y++)
                {
                    if (rows[y] < glassThresh) { top = y; break; }
                }
                for (int y = scanMat.Height - 6; y > scanMat.Height / 2; y--)
                {
                    if (rows[y] < glassThresh) { bottom = y; break; }
                }

                if (top >= 0 && bottom > top && (bottom - top) > 100)
                {
                    return (top, bottom);
                }
            }
            catch { }

            return ((int)(scanMat.Height * 0.15), (int)(scanMat.Height * 0.85));
        }

        /// <summary>
        /// フィルム先端の引き出し黒色露光部（リーダー）の終了位置を検出
        /// </summary>
        private int DetectDarkLeaderEnd(Mat scanMat, OpenCvSharp.Rect trackZone, bool isVertical = true)
        {
            try
            {
                int x = Math.Max(0, Math.Min(trackZone.X, scanMat.Width - 1));
                int y = Math.Max(0, Math.Min(trackZone.Y, scanMat.Height - 1));
                int w = Math.Max(1, Math.Min(trackZone.Width, scanMat.Width - x));
                int h = Math.Max(1, Math.Min(trackZone.Height, scanMat.Height - y));

                using var roi = new Mat(scanMat, new OpenCvSharp.Rect(x, y, w, h));
                using var gray = new Mat();
                Cv2.CvtColor(roi, gray, ColorConversionCodes.BGR2GRAY);

                if (isVertical)
                {
                    using var rowMean = new Mat();
                    Cv2.Reduce(gray, rowMean, ReduceDimension.Column, ReduceTypes.Avg, MatType.CV_32F);
                    float[] vals = new float[h];
                    System.Runtime.InteropServices.Marshal.Copy(rowMean.Data, vals, 0, h);

                    // 先端が真っ黒（露光済みリーダー: 輝度 < 100）の場合、ベース色（> 140）への立ち上がりを探索
                    if (vals.Length > 50 && vals[10] < 100)
                    {
                        for (int i = 20; i < Math.Min(vals.Length, vals.Length / 3); i++)
                        {
                            if (vals[i] > 130)
                            {
                                return i + 10; // リーダー終了位置
                            }
                        }
                    }
                }
                else
                {
                    using var colMean = new Mat();
                    Cv2.Reduce(gray, colMean, ReduceDimension.Row, ReduceTypes.Avg, MatType.CV_32F);
                    float[] vals = new float[w];
                    System.Runtime.InteropServices.Marshal.Copy(colMean.Data, vals, 0, w);

                    if (vals.Length > 50 && vals[10] < 100)
                    {
                        for (int i = 20; i < Math.Min(vals.Length, vals.Length / 3); i++)
                        {
                            if (vals[i] > 130) return i + 10;
                        }
                    }
                }
            }
            catch { }

            return 0;
        }

        /// <summary>
        /// 画像内で最もエッジ活動量（被写体コントラスト）が高い「アンカーコマ」の開始位置を検出
        /// </summary>
        private int FindAnchorFrameStartY(float[] profile, int frameLen, int pitchPx, int leadEnd, int totalLen)
        {
            int bestY = leadEnd;
            double maxEnergy = -1.0;

            int step = Math.Max(2, pitchPx / 30);
            int minSearch = Math.Max(0, leadEnd);
            int maxSearch = Math.Min(totalLen - frameLen, profile.Length - frameLen);

            for (int y = minSearch; y <= maxSearch; y += step)
            {
                double energy = 0;
                int count = 0;
                for (int t = 0; t < frameLen && (y + t) < profile.Length; t++)
                {
                    energy += profile[y + t];
                    count++;
                }

                double avg = count > 0 ? energy / count : 0;
                if (avg > maxEnergy)
                {
                    maxEnergy = avg;
                    bestY = y;
                }
            }

            // 周辺精密化
            int fineMin = Math.Max(minSearch, bestY - step * 2);
            int fineMax = Math.Min(maxSearch, bestY + step * 2);
            for (int y = fineMin; y <= fineMax; y++)
            {
                double energy = 0;
                int count = 0;
                for (int t = 0; t < frameLen && (y + t) < profile.Length; t++)
                {
                    energy += profile[y + t];
                    count++;
                }
                double avg = count > 0 ? energy / count : 0;
                if (avg > maxEnergy)
                {
                    maxEnergy = avg;
                    bestY = y;
                }
            }

            return bestY;
        }

        /// <summary>
        /// アンカーコマ位置から機械規格ピッチ（38.0mm）で前後に同期展開し、全コマの座標を決定
        /// </summary>
        private List<int> GenerateSynchronizedYPositions(
            int anchorY,
            int frameH,
            int pitchPx,
            int leadEnd,
            int totalLen,
            int maxCount,
            float[] profile,
            int snapRadius)
        {
            var rawPositions = new List<int>();

            // アンカーコマ自身を追加
            rawPositions.Add(anchorY);

            // 上方向へ展開 (ピッチ分ずつ遡る)
            int currY = anchorY - pitchPx;
            while (currY >= leadEnd && currY >= 0)
            {
                rawPositions.Add(currY);
                currY -= pitchPx;
            }

            // 下方向へ展開
            currY = anchorY + pitchPx;
            while (currY + frameH <= totalLen)
            {
                rawPositions.Add(currY);
                currY += pitchPx;
            }

            // 昇順ソート
            rawPositions.Sort();

            // 指定コマ数に収める (必要に応じてリーダーに近い方を優先または均等採用)
            if (rawPositions.Count > maxCount)
            {
                // アンカーを含む連続した maxCount 個を選択
                int anchorIdx = rawPositions.IndexOf(anchorY);
                int startIdx = Math.Max(0, anchorIdx - maxCount / 2);
                if (startIdx + maxCount > rawPositions.Count)
                {
                    startIdx = Math.Max(0, rawPositions.Count - maxCount);
                }
                rawPositions = rawPositions.Skip(startIdx).Take(maxCount).ToList();
            }

            // 各コマの位置を局所スリット谷間へスナップ
            var snappedPositions = new List<int>();
            foreach (int pos in rawPositions)
            {
                int snapped = SnapToNearestFrameBoundary(profile, pos, snapRadius);
                snappedPositions.Add(snapped);
            }

            return snappedPositions;
        }

        /// <summary>
        /// フィルム中央の写真領域に沿って長手方向のアクティビティ（水平エッジ密度＋分散）プロファイルを抽出
        /// </summary>
        private float[] ComputeActivityProfile(Mat scanMat, bool isVertical, OpenCvSharp.Rect trackZone)
        {
            try
            {
                int x = Math.Max(0, Math.Min(trackZone.X, scanMat.Width - 1));
                int y = Math.Max(0, Math.Min(trackZone.Y, scanMat.Height - 1));
                int w = Math.Max(1, Math.Min(trackZone.Width, scanMat.Width - x));
                int h = Math.Max(1, Math.Min(trackZone.Height, scanMat.Height - y));

                using var roi = new Mat(scanMat, new OpenCvSharp.Rect(x, y, w, h));
                using var gray = new Mat();
                Cv2.CvtColor(roi, gray, ColorConversionCodes.BGR2GRAY);

                int length = isVertical ? h : w;
                float[] profile = new float[length];

                if (isVertical)
                {
                    using var sobelY = new Mat();
                    Cv2.Sobel(gray, sobelY, MatType.CV_32F, 0, 1, 3);
                    using var absSobel = new Mat();
                    Cv2.ConvertScaleAbs(sobelY, absSobel);

                    using var edgeRowMean = new Mat();
                    Cv2.Reduce(absSobel, edgeRowMean, ReduceDimension.Column, ReduceTypes.Avg, MatType.CV_32F);

                    float[] edgeVals = new float[length];
                    System.Runtime.InteropServices.Marshal.Copy(edgeRowMean.Data, edgeVals, 0, length);
                    Array.Copy(edgeVals, profile, length);
                }
                else
                {
                    using var sobelX = new Mat();
                    Cv2.Sobel(gray, sobelX, MatType.CV_32F, 1, 0, 3);
                    using var absSobel = new Mat();
                    Cv2.ConvertScaleAbs(sobelX, absSobel);

                    using var edgeColMean = new Mat();
                    Cv2.Reduce(absSobel, edgeColMean, ReduceDimension.Row, ReduceTypes.Avg, MatType.CV_32F);

                    float[] edgeVals = new float[length];
                    System.Runtime.InteropServices.Marshal.Copy(edgeColMean.Data, edgeVals, 0, length);
                    Array.Copy(edgeVals, profile, length);
                }

                // 移動平均による平滑化（パーフォレーション残りや微小ノイズを抑制）
                float[] smoothed = new float[length];
                int radius = Math.Max(2, (int)(length * 0.003));
                for (int i = 0; i < length; i++)
                {
                    float sum = 0;
                    int count = 0;
                    for (int r = -radius; r <= radius; r++)
                    {
                        int idx = i + r;
                        if (idx >= 0 && idx < length)
                        {
                            sum += profile[idx];
                            count++;
                        }
                    }
                    smoothed[i] = count > 0 ? sum / count : 0;
                }

                return smoothed;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ComputeActivityProfile error: {ex.Message}");
                int len = isVertical ? scanMat.Height : scanMat.Width;
                return new float[Math.Max(1, len)];
            }
        }

        /// <summary>
        /// 周期パルステンプレートとの相互相関により、写真コマ領域が最も一致する開始オフセットを特定
        /// </summary>
        private int FindOptimalFrameOffset(float[] profile, int frameLen, int pitchPx, int count, int minOffset, int maxOffset)
        {
            if (profile.Length == 0 || frameLen <= 0 || pitchPx <= 0 || count <= 0) return minOffset;

            int bestOffset = minOffset;
            double maxScore = double.NegativeInfinity;

            int step = Math.Max(2, pitchPx / 30);
            int coarseBest = minOffset;

            for (int offset = minOffset; offset <= maxOffset; offset += step)
            {
                if (offset + (count - 1) * pitchPx + frameLen > profile.Length) break;

                double score = 0;
                for (int k = 0; k < count; k++)
                {
                    int frameStart = offset + k * pitchPx;
                    int frameEnd = frameStart + frameLen;
                    int gapEnd = Math.Min(profile.Length, frameStart + pitchPx);

                    double frameEnergy = 0;
                    int fCount = 0;
                    for (int t = frameStart; t < frameEnd && t < profile.Length; t++)
                    {
                        frameEnergy += profile[t];
                        fCount++;
                    }
                    double avgFrame = fCount > 0 ? frameEnergy / fCount : 0;

                    double gapEnergy = 0;
                    int gCount = 0;
                    for (int g = frameEnd; g < gapEnd && g < profile.Length; g++)
                    {
                        gapEnergy += profile[g];
                        gCount++;
                    }
                    double avgGap = gCount > 0 ? gapEnergy / gCount : 0;

                    // コマ内のエッジエネルギーが高く、スリット（谷）のエッジが低いほど高スコア
                    score += (avgFrame - avgGap * 1.6);
                }

                if (score > maxScore)
                {
                    maxScore = score;
                    coarseBest = offset;
                }
            }

            // 周辺を1px刻みで精密探索
            int fineMin = Math.Max(minOffset, coarseBest - step * 2);
            int fineMax = Math.Min(maxOffset, coarseBest + step * 2);

            bestOffset = coarseBest;
            double fineMaxScore = maxScore;

            for (int offset = fineMin; offset <= fineMax; offset++)
            {
                if (offset + (count - 1) * pitchPx + frameLen > profile.Length) break;

                double score = 0;
                for (int k = 0; k < count; k++)
                {
                    int frameStart = offset + k * pitchPx;
                    int frameEnd = frameStart + frameLen;
                    int gapEnd = Math.Min(profile.Length, frameStart + pitchPx);

                    double frameEnergy = 0;
                    int fCount = 0;
                    for (int t = frameStart; t < frameEnd && t < profile.Length; t++)
                    {
                        frameEnergy += profile[t];
                        fCount++;
                    }
                    double avgFrame = fCount > 0 ? frameEnergy / fCount : 0;

                    double gapEnergy = 0;
                    int gCount = 0;
                    for (int g = frameEnd; g < gapEnd && g < profile.Length; g++)
                    {
                        gapEnergy += profile[g];
                        gCount++;
                    }
                    double avgGap = gCount > 0 ? gapEnergy / gCount : 0;

                    score += (avgFrame - avgGap * 1.6);
                }

                if (score > fineMaxScore)
                {
                    fineMaxScore = score;
                    bestOffset = offset;
                }
            }

            return bestOffset;
        }

        /// <summary>
        /// 公称境界の周辺探索範囲から、コマ間スリット（未露光の平坦な谷間）へ磁石吸着
        /// </summary>
        private int SnapToNearestFrameBoundary(float[] profile, int nominalPos, int searchRadius)
        {
            if (nominalPos < 0 || nominalPos >= profile.Length || searchRadius <= 0) return nominalPos;

            int minIdx = nominalPos;
            float minVal = float.MaxValue;

            int start = Math.Max(0, nominalPos - searchRadius);
            int end = Math.Min(profile.Length - 1, nominalPos + searchRadius);

            for (int i = start; i <= end; i++)
            {
                if (profile[i] < minVal)
                {
                    minVal = profile[i];
                    minIdx = i;
                }
            }

            return minIdx;
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
