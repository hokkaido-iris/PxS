using OpenCvSharp;
using IrisPxS.Models;

namespace IrisPxS.Services
{
    /// <summary>
    /// 自動コマ検知および微調整用の各種パラメータ（外部から注入・カスタマイズ可能）
    /// </summary>
    public class DetectionParameters
    {
        // 1. 傾き検知 (Deskew)
        public double CannyThreshold1 { get; set; } = 50;
        public double CannyThreshold2 { get; set; } = 150;
        public int HoughThreshold { get; set; } = 70;
        public double MaxSkewAngleDeg { get; set; } = 45.0;

        // 2. 投影プロファイル & 局所境界探索 (微調整)
        public double SearchWindowPitchRatio { get; set; } = 0.15; // 理論ピッチに対する探索窓幅（±15%）
        public float MinEdgeGradientThreshold { get; set; } = 1.5f; // 境界として採用する最小勾配強度

        // 3. 幾何学的ヒューリスティクス（フェイルセーフ許容範囲）
        public double AspectRatioTolerance { get; set; } = 0.20; // 理論アスペクト比に対する許容誤差 (±20%)
        public double DimensionTolerance { get; set; } = 0.15;   // 理論幅・理論高さに対する許容誤差 (±15%)
        public double MinAreaRatio { get; set; } = 0.70;        // 理論面積の 70%
        public double MaxAreaRatio { get; set; } = 1.30;        // 理論面積の 130%

        // 4. マージン・パーフォレーション除外比率
        public double TrackMarginRatio { get; set; } = 0.05;    // トラック内側マージン比率
    }

    public class FrameDetectorService
    {
        /// <summary>
        /// フィルムとメディアがない部分（透過光素抜けガラス）のコントラスト境界から斜めの傾き角（Skew Angle）を検出し、
        /// スキャン画像を自動正立（De-skew）した上で、等間隔配置と各コマの画像範囲による自動微調整ハイブリッド方式でコマ枠を生成する
        /// </summary>
        public (Mat straightenedMat, double skewAngle, List<OpenCvSharp.Rect> frames) DetectAndStraighten(
            Mat scanMat,
            FilmFormat format,
            int dpi = 0,
            DetectionParameters? parameters = null)
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

            // 1. フィルム長辺エッジから傾き角（度）を精密検出 (ハフ変換)
            double skewAngle = DetectFilmSkewAngleFromMediaBoundary(scanMat, parameters);

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

            // 3. 正立された画像から、等間隔ベースグリッド配置 ＋ 局所画像境界自動微調整によりコマ枠を高精度生成
            var frames = DetectFramesOnStraightened(workingMat, format, dpi, parameters);

            return (workingMat, skewAngle, frames);
        }

        /// <summary>
        /// フィルムと素抜けガラスの境界線から、フィルムストリップの物理的な傾き角度（-45度〜+45度対応）を精密に検出する
        /// ハフ直線変換(HoughLinesP)により、パーフォレーションやフィルム端の直線の角度中央値を算出
        /// </summary>
        public double DetectFilmSkewAngleFromMediaBoundary(Mat scanMat, DetectionParameters? parameters = null)
        {
            try
            {
                var p = parameters ?? new DetectionParameters();

                // 高速かつ大域的な解析のため、最大長辺 800px に縮小
                double maxDim = Math.Max(scanMat.Width, scanMat.Height);
                double scale = 800.0 / maxDim;
                int smallW = Math.Max(10, (int)(scanMat.Width * scale));
                int smallH = Math.Max(10, (int)(scanMat.Height * scale));

                using var small = new Mat();
                Cv2.Resize(scanMat, small, new OpenCvSharp.Size(smallW, smallH), 0, 0, InterpolationFlags.Area);

                using var gray = new Mat();
                Cv2.CvtColor(small, gray, ColorConversionCodes.BGR2GRAY);

                // Canny エッジ検出 (パラメータから閾値適用)
                using var edges = new Mat();
                Cv2.Canny(gray, edges, p.CannyThreshold1, p.CannyThreshold2);

                bool isVertical = smallH >= smallW;
                int minLineLen = (int)(isVertical ? smallH * 0.12 : smallW * 0.12);

                // 確率的ハフ変換で長辺直線セグメントを検出
                var lines = Cv2.HoughLinesP(edges, 1, Math.PI / 180.0, threshold: p.HoughThreshold, minLineLength: minLineLen, maxLineGap: 20);

                var angles = new List<double>();

                foreach (var line in lines)
                {
                    double dx = line.P2.X - line.P1.X;
                    double dy = line.P2.Y - line.P1.Y;
                    double len = Math.Sqrt(dx * dx + dy * dy);
                    if (len < minLineLen) continue;

                    if (isVertical)
                    {
                        if (dy < 0) { dx = -dx; dy = -dy; }
                        // 垂直に近い直線 (|dx/dy| < 0.6)
                        if (Math.Abs(dx / (dy > 0 ? dy : 1.0)) < 0.6)
                        {
                            double ang = Math.Atan2(dx, dy) * (180.0 / Math.PI);
                            if (Math.Abs(ang) <= 45.0) angles.Add(ang);
                        }
                    }
                    else
                    {
                        if (dx < 0) { dx = -dx; dy = -dy; }
                        // 水平に近い直線 (|dy/dx| < 0.6)
                        if (Math.Abs(dy / (dx > 0 ? dx : 1.0)) < 0.6)
                        {
                            double ang = Math.Atan2(dy, dx) * (180.0 / Math.PI);
                            if (Math.Abs(ang) <= 45.0) angles.Add(ang);
                        }
                    }
                }

                if (angles.Count > 0)
                {
                    angles.Sort();
                    double median = angles[angles.Count / 2];
                    return Math.Round(median, 2);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DetectFilmSkewAngleFromMediaBoundary error: {ex.Message}");
            }

            return 0.0;
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
        /// フィルムストリップの物理全幅（パーフォレーション穴・余白を含む全幅: mm）を取得
        /// </summary>
        public static double GetFilmStripTotalWidthMm(FilmFormat format)
        {
            return format.Category switch
            {
                FilmSizeCategory.Size135 => 35.0,
                FilmSizeCategory.Size110 => 16.0,
                FilmSizeCategory.Size120 => 61.5,
                FilmSizeCategory.Size127 => 46.0,
                _ => 35.0
            };
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
                    marginMm = 2.0; // 38.0mm ピッチ (8パーフォレーション)
                    break;
                case FilmFormatType.Format135_Half:
                    dimAcross = 24.0;
                    dimAlong = 18.0;
                    marginMm = 1.5; // 19.5mm ピッチ
                    break;
                case FilmFormatType.Format120_645:
                    dimAcross = 56.0;
                    dimAlong = 41.5;
                    marginMm = 5.0; // 46.5mm ピッチ
                    break;
                case FilmFormatType.Format120_66:
                    dimAcross = 56.0;
                    dimAlong = 56.0;
                    marginMm = 6.0; // 62.0mm ピッチ
                    break;
                case FilmFormatType.Format120_67:
                    dimAcross = 56.0;
                    dimAlong = 70.0;
                    marginMm = 6.0; // 76.0mm ピッチ
                    break;
                case FilmFormatType.Format120_69:
                    dimAcross = 56.0;
                    dimAlong = 84.0;
                    marginMm = 6.0; // 90.0mm ピッチ
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
                    // 110ポケットフィルム国際規格 (ISO 844): 13x17mm, ピッチ 25.0mm (マージン 8.0mm)
                    dimAcross = 13.0;
                    dimAlong = 17.0;
                    marginMm = 8.0; // 17.0 + 8.0 = 25.0mm
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
        /// 正立された画像上でフィルムストリップ位置を特定し、コーム相関(Comb Correlation)による等間隔ベースグリッド配置と、
        /// 各コマ周辺の局所画像境界（エッジ・投影プロファイル）検出による自動微調整ハイブリッド方式でコマ枠を配置
        /// </summary>
        public List<OpenCvSharp.Rect> DetectFramesOnStraightened(
            Mat scanMat,
            FilmFormat format,
            int dpi,
            DetectionParameters? parameters = null)
        {
            var frames = new List<OpenCvSharp.Rect>();
            if (scanMat == null || scanMat.Empty()) return frames;

            bool isVertical = scanMat.Height >= scanMat.Width;

            // DPI 不整合の自動検出と是正
            GetFormatDimensions(format, isVertical, dpi, out int targetW, out int targetH, out int pitchPx);

            if ((isVertical && targetH >= scanMat.Height) || (!isVertical && targetW >= scanMat.Width))
            {
                int maxDim = Math.Max(scanMat.Width, scanMat.Height);
                dpi = (int)Math.Round((double)maxDim / (240.0 / 25.4));
                if (dpi < 100) dpi = 150;
                GetFormatDimensions(format, isVertical, dpi, out targetW, out targetH, out pitchPx);
            }

            int desiredCount = format.DefaultFramesPerStrip > 0 ? format.DefaultFramesPerStrip : 6;
            double stripTotalWidthMm = GetFilmStripTotalWidthMm(format);
            int expectedStripPx = (int)Math.Round(stripTotalWidthMm * dpi / 25.4);

            if (isVertical)
            {
                int frameW = Math.Min(targetW, scanMat.Width - 4);
                int frameH = targetH;
                int gapH = Math.Max(4, pitchPx - frameH);

                // 1. フィルムストリップの正確な左右境界 (X_L, X_R) の検出
                var (filmLeft, filmRight) = DetectFilmHorizontalEdges(scanMat, expectedStripPx);

                int startX;
                if (filmRight > filmLeft && (filmRight - filmLeft) >= frameW)
                {
                    // フィルム帯の中央に写真トラックを配置
                    startX = filmLeft + ((filmRight - filmLeft) - frameW) / 2;
                }
                else
                {
                    startX = (scanMat.Width - frameW) / 2;
                }
                startX = Math.Max(0, Math.Min(startX, scanMat.Width - frameW));

                // 2. 写真トラックの垂直プロファイル（輝度 ＆ Sobel-Y エッジ）を抽出
                int trackX = startX + (int)(frameW * 0.10);
                int trackW = Math.Max(10, (int)(frameW * 0.80));
                var trackZone = new OpenCvSharp.Rect(trackX, 0, trackW, scanMat.Height);

                var (rowLum, rowEdge) = ComputeTrackProfiles(scanMat, true, trackZone);

                // 3. フィルム有効開始点（ガラス余白・先端黒リーダーの終了）を検出
                int filmStart = DetectFilmStartY(rowLum, scanMat.Height);

                // 4. コーム相関（Comb Correlation）により、最も整合する基準オフセット（位相）を特定
                int bestAnchorY = FindOptimalCombPhase(rowLum, rowEdge, frameH, pitchPx, gapH, filmStart, scanMat.Height, isVertical: true);

                // 5. 決定した位相から、フィルム規格固定ピッチで全コマを等間隔に配置（ベースグリッド）
                var yCoords = GenerateFixedPitchPositions(bestAnchorY, frameH, pitchPx, filmStart, scanMat.Height, desiredCount);

                foreach (int y in yCoords)
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

                // 6. 等間隔配置をベースにしつつ、各コマ周辺の画像境界（エッジ・投影プロファイル）を検出して自動微調整
                frames = RefineFrameBoundaries(scanMat, frames, format, isVertical: true, frameW, frameH, pitchPx, parameters);
            }
            else
            {
                // 横ストリップ
                int frameW = targetW;
                int frameH = Math.Min(targetH, scanMat.Height - 4);
                int gapW = Math.Max(4, pitchPx - frameW);

                var (filmTop, filmBottom) = DetectFilmVerticalEdges(scanMat, expectedStripPx);

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

                var (colLum, colEdge) = ComputeTrackProfiles(scanMat, false, trackZone);
                int filmStart = DetectFilmStartX(colLum, scanMat.Width);

                int bestAnchorX = FindOptimalCombPhase(colLum, colEdge, frameW, pitchPx, gapW, filmStart, scanMat.Width, isVertical: false);
                var xCoords = GenerateFixedPitchPositions(bestAnchorX, frameW, pitchPx, filmStart, scanMat.Width, desiredCount);

                foreach (int x in xCoords)
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

                // 6. 等間隔配置をベースにしつつ、各コマ周辺の画像境界（エッジ・投影プロファイル）を検出して自動微調整
                frames = RefineFrameBoundaries(scanMat, frames, format, isVertical: false, frameW, frameH, pitchPx, parameters);
            }

            return frames;
        }

        /// <summary>
        /// 透過スキャン画像からフィルムストリップの正確な左右境界 (X_L, X_R) を検出
        /// 期待される物理全幅 expectedWidthPx をヒントに最も合致するフィルム帯を特定
        /// </summary>
        public (int Left, int Right) DetectFilmHorizontalEdges(Mat scanMat, int expectedWidthPx)
        {
            try
            {
                using var gray = new Mat();
                Cv2.CvtColor(scanMat, gray, ColorConversionCodes.BGR2GRAY);

                // 中央付近の高さ50%領域で水平プロファイルを計算（上下端の余白を回避）
                int midY = (int)(scanMat.Height * 0.20);
                int midH = (int)(scanMat.Height * 0.60);
                using var roi = new Mat(gray, new OpenCvSharp.Rect(0, midY, scanMat.Width, midH));

                using var colMean = new Mat();
                Cv2.Reduce(roi, colMean, ReduceDimension.Row, ReduceTypes.Avg, MatType.CV_32F);

                int w = scanMat.Width;
                float[] cols = new float[w];
                System.Runtime.InteropServices.Marshal.Copy(colMean.Data, cols, 0, w);

                // 素抜けガラスの輝度閾値（通常 > 215）
                float maxVal = cols.Max();
                float glassThresh = Math.Max(205f, maxVal * 0.88f);

                // 勾配（エッジ）プロファイルを計算
                float[] grad = new float[w];
                for (int x = 1; x < w - 1; x++)
                {
                    grad[x] = cols[x + 1] - cols[x - 1]; // 正: 暗→明 (右端), 負: 明→暗 (左端)
                }

                // 期待される幅に近いペア (left, right) を探索
                int bestLeft = -1;
                int bestRight = -1;
                double bestScore = double.MinValue;

                int minW = (int)(expectedWidthPx * 0.70);
                int maxW = (int)(expectedWidthPx * 1.30);

                for (int left = 5; left < w - minW; left++)
                {
                    // 左端: ガラスからフィルムへの急峻な立ち下がり
                    if (cols[left] >= glassThresh || grad[left] >= 0) continue;

                    for (int span = minW; span <= maxW && (left + span) < w - 5; span += 2)
                    {
                        int right = left + span;
                        // 右端: フィルムからガラスへの急峻な立ち上がり
                        if (cols[right] >= glassThresh || grad[right] <= 0) continue;

                        // フィルム内部 (left+10 〜 right-10) の平均輝度がガラスより明確に暗いこと
                        double innerSum = 0;
                        int innerCount = 0;
                        for (int k = left + 10; k <= right - 10; k += 4)
                        {
                            innerSum += cols[k];
                            innerCount++;
                        }
                        double innerAvg = innerCount > 0 ? innerSum / innerCount : 255;
                        if (innerAvg > glassThresh - 10) continue;

                        double widthMatchScore = 1.0 - Math.Abs(span - expectedWidthPx) / (double)expectedWidthPx;
                        double edgeScore = (-grad[left]) + grad[right];
                        double totalScore = edgeScore * 0.5 + widthMatchScore * 100.0;

                        if (totalScore > bestScore)
                        {
                            bestScore = totalScore;
                            bestLeft = left;
                            bestRight = right;
                        }
                    }
                }

                if (bestLeft >= 0 && bestRight > bestLeft)
                {
                    return (bestLeft, bestRight);
                }

                // フォールバック: 単純二値化での立ち下がり・立ち上がり
                int fbLeft = -1, fbRight = -1;
                for (int x = 5; x < w; x++)
                {
                    if (cols[x] < glassThresh) { fbLeft = x; break; }
                }
                for (int x = w - 6; x >= 0; x--)
                {
                    if (cols[x] < glassThresh) { fbRight = x; break; }
                }

                if (fbLeft >= 0 && fbRight > fbLeft && (fbRight - fbLeft) > 50)
                {
                    return (fbLeft, fbRight);
                }
            }
            catch { }

            // 最終セーフガード
            int defMargin = Math.Max(10, (scanMat.Width - expectedWidthPx) / 2);
            return (defMargin, scanMat.Width - defMargin);
        }

        /// <summary>
        /// 横ストリップ時の上下境界 (Top, Bottom) を検出
        /// </summary>
        public (int Top, int Bottom) DetectFilmVerticalEdges(Mat scanMat, int expectedWidthPx)
        {
            try
            {
                using var gray = new Mat();
                Cv2.CvtColor(scanMat, gray, ColorConversionCodes.BGR2GRAY);

                int midX = (int)(scanMat.Width * 0.20);
                int midW = (int)(scanMat.Width * 0.60);
                using var roi = new Mat(gray, new OpenCvSharp.Rect(midX, 0, midW, scanMat.Height));

                using var rowMean = new Mat();
                Cv2.Reduce(roi, rowMean, ReduceDimension.Column, ReduceTypes.Avg, MatType.CV_32F);

                int h = scanMat.Height;
                float[] rows = new float[h];
                System.Runtime.InteropServices.Marshal.Copy(rowMean.Data, rows, 0, h);

                float maxVal = rows.Max();
                float glassThresh = Math.Max(205f, maxVal * 0.88f);

                int top = -1, bottom = -1;
                for (int y = 5; y < h; y++)
                {
                    if (rows[y] < glassThresh) { top = y; break; }
                }
                for (int y = h - 6; y >= 0; y--)
                {
                    if (rows[y] < glassThresh) { bottom = y; break; }
                }

                if (top >= 0 && bottom > top && (bottom - top) > 50)
                {
                    return (top, bottom);
                }
            }
            catch { }

            int defMargin = Math.Max(10, (scanMat.Height - expectedWidthPx) / 2);
            return (defMargin, scanMat.Height - defMargin);
        }

        /// <summary>
        /// 写真トラックの輝度プロファイルおよび Sobel エッジプロファイルを同時に抽出
        /// </summary>
        private (float[] Lum, float[] Edge) ComputeTrackProfiles(Mat scanMat, bool isVertical, OpenCvSharp.Rect trackZone)
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
                float[] lum = new float[length];
                float[] edge = new float[length];

                if (isVertical)
                {
                    using var rowMean = new Mat();
                    Cv2.Reduce(gray, rowMean, ReduceDimension.Column, ReduceTypes.Avg, MatType.CV_32F);
                    System.Runtime.InteropServices.Marshal.Copy(rowMean.Data, lum, 0, length);

                    using var sobelY = new Mat();
                    Cv2.Sobel(gray, sobelY, MatType.CV_32F, 0, 1, 3);
                    using var absSobel = new Mat();
                    Cv2.ConvertScaleAbs(sobelY, absSobel);

                    using var edgeMean = new Mat();
                    Cv2.Reduce(absSobel, edgeMean, ReduceDimension.Column, ReduceTypes.Avg, MatType.CV_32F);
                    System.Runtime.InteropServices.Marshal.Copy(edgeMean.Data, edge, 0, length);
                }
                else
                {
                    using var colMean = new Mat();
                    Cv2.Reduce(gray, colMean, ReduceDimension.Row, ReduceTypes.Avg, MatType.CV_32F);
                    System.Runtime.InteropServices.Marshal.Copy(colMean.Data, lum, 0, length);

                    using var sobelX = new Mat();
                    Cv2.Sobel(gray, sobelX, MatType.CV_32F, 1, 0, 3);
                    using var absSobel = new Mat();
                    Cv2.ConvertScaleAbs(sobelX, absSobel);

                    using var edgeMean = new Mat();
                    Cv2.Reduce(absSobel, edgeMean, ReduceDimension.Row, ReduceTypes.Avg, MatType.CV_32F);
                    System.Runtime.InteropServices.Marshal.Copy(edgeMean.Data, edge, 0, length);
                }

                return (lum, edge);
            }
            catch
            {
                int len = isVertical ? scanMat.Height : scanMat.Width;
                return (new float[len], new float[len]);
            }
        }

        /// <summary>
        /// ガラス余白および先端の真っ黒な露光リーダー部の終了位置（写真領域の開始点）を検出
        /// </summary>
        public int DetectFilmStartY(float[] lum, int totalH)
        {
            if (lum == null || lum.Length < 50) return 0;

            int filmEntryY = 0;

            // 1. 素抜けガラス（輝度 > 210）からフィルム（輝度 < 200）への進入点を検出
            for (int y = 5; y < Math.Min(lum.Length - 20, totalH / 3); y++)
            {
                if (lum[y] < 205)
                {
                    filmEntryY = y;
                    break;
                }
            }

            // 2. 先端が真っ黒な露光リーダー（輝度 < 80）である場合、ベース（輝度 > 130）への立ち上がりを探索
            if (filmEntryY < lum.Length / 3)
            {
                bool hasDarkLeader = false;
                for (int y = filmEntryY; y < Math.Min(lum.Length, filmEntryY + 300); y++)
                {
                    if (lum[y] < 60)
                    {
                        hasDarkLeader = true;
                        break;
                    }
                }

                if (hasDarkLeader)
                {
                    for (int y = filmEntryY + 20; y < Math.Min(lum.Length - 30, totalH / 2); y++)
                    {
                        if (lum[y] > 130 && lum[y - 10] < 80)
                        {
                            return y + 10; // リーダー終了位置
                        }
                    }
                }
            }

            return filmEntryY > 0 ? filmEntryY : 0;
        }

        /// <summary>
        /// 横ストリップ時のフィルム開始Xを検出
        /// </summary>
        public int DetectFilmStartX(float[] lum, int totalW)
        {
            return DetectFilmStartY(lum, totalW);
        }

        /// <summary>
        /// コーム相関（Comb Correlation）により、最も整合する基準オフセット（位相）を特定
        /// コマ内（被写体エッジあり・ネガでは露光濃度あり）と、コマ間スリット（未露光ベース・平坦・高輝度）の対比を最大化
        /// </summary>
        public int FindOptimalCombPhase(
            float[] lum,
            float[] edge,
            int frameLen,
            int pitchPx,
            int gapLen,
            int minStart,
            int totalLen,
            bool isVertical)
        {
            if (lum.Length == 0 || frameLen <= 0 || pitchPx <= 0) return minStart;

            int bestOffset = minStart;
            double maxScore = double.NegativeInfinity;

            int searchStart = minStart;
            int searchEnd = Math.Min(minStart + pitchPx, lum.Length - frameLen);

            for (int offset = searchStart; offset <= searchEnd; offset++)
            {
                double score = 0;
                int count = 0;

                for (int k = 0; k < 6; k++)
                {
                    int fStart = offset + k * pitchPx;
                    int fEnd = fStart + frameLen;
                    int gEnd = fStart + pitchPx;
                    if (gEnd >= lum.Length || gEnd >= totalLen) break;

                    // コマ内平均エッジ ＆ 平均輝度
                    double fEdgeSum = 0;
                    double fLumSum = 0;
                    for (int i = fStart; i < fEnd; i++)
                    {
                        fEdgeSum += edge[i];
                        fLumSum += lum[i];
                    }
                    double fEdgeAvg = fEdgeSum / frameLen;
                    double fLumAvg = fLumSum / frameLen;

                    // スリット内平均エッジ ＆ 平均輝度
                    double gEdgeSum = 0;
                    double gLumSum = 0;
                    for (int i = fEnd; i < gEnd; i++)
                    {
                        gEdgeSum += edge[i];
                        gLumSum += lum[i];
                    }
                    double gEdgeAvg = gapLen > 0 ? gEdgeSum / gapLen : 0;
                    double gLumAvg = gapLen > 0 ? gLumSum / gapLen : 0;

                    // 評価指標:
                    // 1. スリット輝度 - コマ輝度 (ネガフィルムでは未露光スリットが最も明るい)
                    double lumDiff = (gLumAvg - fLumAvg);
                    // 2. コマ内エッジ - スリット内エッジ (被写体ディテールはコマ内にある)
                    double edgeDiff = (fEdgeAvg - gEdgeAvg * 1.8);

                    score += (lumDiff * 0.7 + edgeDiff * 1.5);
                    count++;
                }

                if (count >= 1 && score > maxScore)
                {
                    maxScore = score;
                    bestOffset = offset;
                }
            }

            return bestOffset;
        }

        /// <summary>
        /// 決定した基準位相から、フィルム規格固定ピッチで全コマを等間隔に配置
        /// </summary>
        private List<int> GenerateFixedPitchPositions(
            int anchorPos,
            int frameLen,
            int pitchPx,
            int minLimit,
            int maxLimit,
            int maxCount)
        {
            var positions = new List<int>();

            // 有効範囲内の最も先頭（手前）のコマ位置まで遡る
            int firstPos = anchorPos;
            while (firstPos - pitchPx >= minLimit)
            {
                firstPos -= pitchPx;
            }

            // 先頭から順に maxLimit 内で等間隔に配置
            int curr = firstPos;
            while (curr + frameLen <= maxLimit && positions.Count < maxCount)
            {
                positions.Add(curr);
                curr += pitchPx;
            }

            return positions;
        }

        /// <summary>
        /// 等間隔配置（ベースグリッド）をアンカーとし、各コマ周辺の局所投影プロファイル・エッジ勾配で
        /// 写真の実際の境界（コマ間スリット・左右アパーチャ端）を自動検出し、高精度に微調整する
        /// </summary>
        public List<OpenCvSharp.Rect> RefineFrameBoundaries(
            Mat scanMat,
            List<OpenCvSharp.Rect> baseFrames,
            FilmFormat format,
            bool isVertical,
            int targetW,
            int targetH,
            int pitchPx,
            DetectionParameters? parameters = null)
        {
            if (baseFrames == null || baseFrames.Count == 0 || scanMat == null || scanMat.Empty())
            {
                return baseFrames ?? new List<OpenCvSharp.Rect>();
            }

            var p = parameters ?? new DetectionParameters();
            var refinedList = new List<OpenCvSharp.Rect>();

            using var gray = new Mat();
            Cv2.CvtColor(scanMat, gray, ColorConversionCodes.BGR2GRAY);

            if (isVertical)
            {
                // 縦ストリップ: Y軸＝コマ送り方向（巻き上げムラあり）、X軸＝アパーチャ幅方向（物理幅固定）
                int searchWinY = Math.Max(25, (int)(pitchPx * p.SearchWindowPitchRatio));
                int searchWinX = Math.Max(15, (int)(targetW * 0.12));

                // 1. 各コマの長手方向（Top, Bottom）の境界を局所プロファイル勾配で探索
                var refinedRangesY = new List<(int Top, int Bottom, bool Success)>();

                for (int i = 0; i < baseFrames.Count; i++)
                {
                    var r = baseFrames[i];
                    int baseTop = r.Y;
                    int baseBot = r.Y + r.Height;

                    // 上辺 (Top) 探索: baseTop の前後 ±searchWinY
                    int topMin = Math.Max(0, baseTop - searchWinY);
                    int topMax = Math.Min(scanMat.Height, baseTop + searchWinY);
                    int detectedTop = baseTop;
                    float maxTopGrad = 0;

                    if (topMax > topMin + 10)
                    {
                        using var topRoi = new Mat(gray, new OpenCvSharp.Rect(r.X, topMin, r.Width, topMax - topMin));
                        using var topMean = new Mat();
                        Cv2.Reduce(topRoi, topMean, ReduceDimension.Column, ReduceTypes.Avg, MatType.CV_32F);
                        float[] topVals = new float[topMax - topMin];
                        System.Runtime.InteropServices.Marshal.Copy(topMean.Data, topVals, 0, topVals.Length);

                        for (int k = 1; k < topVals.Length - 1; k++)
                        {
                            float diff = Math.Abs(topVals[k + 1] - topVals[k - 1]);
                            if (diff > maxTopGrad)
                            {
                                maxTopGrad = diff;
                                detectedTop = topMin + k;
                            }
                        }
                    }

                    // 下辺 (Bottom) 探索: baseBot の前後 ±searchWinY
                    int botMin = Math.Max(0, baseBot - searchWinY);
                    int botMax = Math.Min(scanMat.Height, baseBot + searchWinY);
                    int detectedBot = baseBot;
                    float maxBotGrad = 0;

                    if (botMax > botMin + 10)
                    {
                        using var botRoi = new Mat(gray, new OpenCvSharp.Rect(r.X, botMin, r.Width, botMax - botMin));
                        using var botMean = new Mat();
                        Cv2.Reduce(botRoi, botMean, ReduceDimension.Column, ReduceTypes.Avg, MatType.CV_32F);
                        float[] botVals = new float[botMax - botMin];
                        System.Runtime.InteropServices.Marshal.Copy(botMean.Data, botVals, 0, botVals.Length);

                        for (int k = 1; k < botVals.Length - 1; k++)
                        {
                            float diff = Math.Abs(botVals[k + 1] - botVals[k - 1]);
                            if (diff > maxBotGrad)
                            {
                                maxBotGrad = diff;
                                detectedBot = botMin + k;
                            }
                        }
                    }

                    // 境界エッジの信頼性判定
                    bool topConfident = maxTopGrad >= p.MinEdgeGradientThreshold;
                    bool botConfident = maxBotGrad >= p.MinEdgeGradientThreshold;

                    int finalTop = baseTop;
                    int finalBot = baseBot;

                    if (topConfident && botConfident && detectedBot > detectedTop + (int)(targetH * 0.70))
                    {
                        finalTop = detectedTop;
                        finalBot = detectedBot;
                    }
                    else if (topConfident && !botConfident)
                    {
                        finalTop = detectedTop;
                        finalBot = Math.Min(scanMat.Height, detectedTop + targetH);
                    }
                    else if (!topConfident && botConfident)
                    {
                        finalBot = detectedBot;
                        finalTop = Math.Max(0, detectedBot - targetH);
                    }

                    // 幾何学的ヒューリスティクス検証: コマ高が理論値の許容範囲内か
                    int frameH = finalBot - finalTop;
                    bool sizeValid = Math.Abs(frameH - targetH) <= (targetH * p.DimensionTolerance);

                    if (sizeValid)
                    {
                        refinedRangesY.Add((finalTop, finalBot, true));
                    }
                    else
                    {
                        // 検証不合格時は理論ベース位置にフェイルセーフ
                        refinedRangesY.Add((baseTop, baseBot, false));
                    }
                }

                // 2. 幅方向（X軸）の最適化:
                // カメラの露光ゲート幅は全コマ共通のため、信頼性の高いコマ群の中央値から X と Width を決定
                int bestX = baseFrames[0].X;
                int bestW = targetW;

                var xOffsets = new List<int>();
                for (int i = 0; i < baseFrames.Count; i++)
                {
                    var r = baseFrames[i];
                    var (top, bot, _) = refinedRangesY[i];
                    int midH = bot - top;
                    if (midH < 30) continue;

                    using var frameMidRoi = new Mat(gray, new OpenCvSharp.Rect(0, top + (int)(midH * 0.15), scanMat.Width, (int)(midH * 0.70)));
                    using var rowMeanX = new Mat();
                    Cv2.Reduce(frameMidRoi, rowMeanX, ReduceDimension.Row, ReduceTypes.Avg, MatType.CV_32F);
                    float[] rowValsX = new float[scanMat.Width];
                    System.Runtime.InteropServices.Marshal.Copy(rowMeanX.Data, rowValsX, 0, scanMat.Width);

                    int leftMin = Math.Max(0, r.X - searchWinX);
                    int leftMax = Math.Min(scanMat.Width, r.X + searchWinX);
                    float maxLeftGrad = 0;
                    int detectedLeftX = r.X;
                    for (int k = leftMin + 1; k < leftMax - 1; k++)
                    {
                        float diff = Math.Abs(rowValsX[k + 1] - rowValsX[k - 1]);
                        if (diff > maxLeftGrad) { maxLeftGrad = diff; detectedLeftX = k; }
                    }

                    int rightMin = Math.Max(0, (r.X + r.Width) - searchWinX);
                    int rightMax = Math.Min(scanMat.Width, (r.X + r.Width) + searchWinX);
                    float maxRightGrad = 0;
                    int detectedRightX = r.X + r.Width;
                    for (int k = rightMin + 1; k < rightMax - 1; k++)
                    {
                        float diff = Math.Abs(rowValsX[k + 1] - rowValsX[k - 1]);
                        if (diff > maxRightGrad) { maxRightGrad = diff; detectedRightX = k; }
                    }

                    if (maxLeftGrad >= p.MinEdgeGradientThreshold && maxRightGrad >= p.MinEdgeGradientThreshold)
                    {
                        int span = detectedRightX - detectedLeftX;
                        if (Math.Abs(span - targetW) <= targetW * p.DimensionTolerance)
                        {
                            xOffsets.Add(detectedLeftX);
                        }
                    }
                }

                if (xOffsets.Count > 0)
                {
                    xOffsets.Sort();
                    bestX = xOffsets[xOffsets.Count / 2]; // 中央値
                }

                // 3. 最終矩形の組み立て & コマ間重なり防止
                int prevBot = -1;
                for (int i = 0; i < baseFrames.Count; i++)
                {
                    var (top, bot, _) = refinedRangesY[i];
                    if (prevBot >= 0 && top < prevBot + 2)
                    {
                        top = prevBot + 2;
                    }
                    if (bot <= top + 20)
                    {
                        bot = top + targetH;
                    }

                    int clampedTop = Math.Max(0, Math.Min(top, scanMat.Height - 10));
                    int clampedBot = Math.Min(scanMat.Height, Math.Max(clampedTop + 10, bot));
                    int clampedX = Math.Max(0, Math.Min(bestX, scanMat.Width - bestW));
                    int clampedW = Math.Min(bestW, scanMat.Width - clampedX);
                    int clampedH = clampedBot - clampedTop;

                    refinedList.Add(new OpenCvSharp.Rect(clampedX, clampedTop, clampedW, clampedH));
                    prevBot = clampedBot;
                }
            }
            else
            {
                // 横ストリップ: X軸＝コマ送り方向（巻き上げムラあり）、Y軸＝アパーチャ幅方向
                int searchWinX = Math.Max(25, (int)(pitchPx * p.SearchWindowPitchRatio));
                int searchWinY = Math.Max(15, (int)(targetH * 0.12));

                var refinedRangesX = new List<(int Left, int Right, bool Success)>();

                for (int i = 0; i < baseFrames.Count; i++)
                {
                    var r = baseFrames[i];
                    int baseLeft = r.X;
                    int baseRight = r.X + r.Width;

                    // 左辺 (Left) 探索
                    int leftMin = Math.Max(0, baseLeft - searchWinX);
                    int leftMax = Math.Min(scanMat.Width, baseLeft + searchWinX);
                    int detectedLeft = baseLeft;
                    float maxLeftGrad = 0;

                    if (leftMax > leftMin + 10)
                    {
                        using var leftRoi = new Mat(gray, new OpenCvSharp.Rect(leftMin, r.Y, leftMax - leftMin, r.Height));
                        using var leftMean = new Mat();
                        Cv2.Reduce(leftRoi, leftMean, ReduceDimension.Row, ReduceTypes.Avg, MatType.CV_32F);
                        float[] leftVals = new float[leftMax - leftMin];
                        System.Runtime.InteropServices.Marshal.Copy(leftMean.Data, leftVals, 0, leftVals.Length);

                        for (int k = 1; k < leftVals.Length - 1; k++)
                        {
                            float diff = Math.Abs(leftVals[k + 1] - leftVals[k - 1]);
                            if (diff > maxLeftGrad)
                            {
                                maxLeftGrad = diff;
                                detectedLeft = leftMin + k;
                            }
                        }
                    }

                    // 右辺 (Right) 探索
                    int rightMin = Math.Max(0, baseRight - searchWinX);
                    int rightMax = Math.Min(scanMat.Width, baseRight + searchWinX);
                    int detectedRight = baseRight;
                    float maxRightGrad = 0;

                    if (rightMax > rightMin + 10)
                    {
                        using var rightRoi = new Mat(gray, new OpenCvSharp.Rect(rightMin, r.Y, rightMax - rightMin, r.Height));
                        using var rightMean = new Mat();
                        Cv2.Reduce(rightRoi, rightMean, ReduceDimension.Row, ReduceTypes.Avg, MatType.CV_32F);
                        float[] rightVals = new float[rightMax - rightMin];
                        System.Runtime.InteropServices.Marshal.Copy(rightMean.Data, rightVals, 0, rightVals.Length);

                        for (int k = 1; k < rightVals.Length - 1; k++)
                        {
                            float diff = Math.Abs(rightVals[k + 1] - rightVals[k - 1]);
                            if (diff > maxRightGrad)
                            {
                                maxRightGrad = diff;
                                detectedRight = rightMin + k;
                            }
                        }
                    }

                    bool leftConfident = maxLeftGrad >= p.MinEdgeGradientThreshold;
                    bool rightConfident = maxRightGrad >= p.MinEdgeGradientThreshold;

                    int finalLeft = baseLeft;
                    int finalRight = baseRight;

                    if (leftConfident && rightConfident && detectedRight > detectedLeft + (int)(targetW * 0.70))
                    {
                        finalLeft = detectedLeft;
                        finalRight = detectedRight;
                    }
                    else if (leftConfident && !rightConfident)
                    {
                        finalLeft = detectedLeft;
                        finalRight = Math.Min(scanMat.Width, detectedLeft + targetW);
                    }
                    else if (!leftConfident && rightConfident)
                    {
                        finalRight = detectedRight;
                        finalLeft = Math.Max(0, detectedRight - targetW);
                    }

                    int frameW = finalRight - finalLeft;
                    bool sizeValid = Math.Abs(frameW - targetW) <= (targetW * p.DimensionTolerance);

                    if (sizeValid)
                    {
                        refinedRangesX.Add((finalLeft, finalRight, true));
                    }
                    else
                    {
                        refinedRangesX.Add((baseLeft, baseRight, false));
                    }
                }

                // 幅方向（Y軸）の最適化
                int bestY = baseFrames[0].Y;
                int bestH = targetH;

                var yOffsets = new List<int>();
                for (int i = 0; i < baseFrames.Count; i++)
                {
                    var r = baseFrames[i];
                    var (left, right, _) = refinedRangesX[i];
                    int midW = right - left;
                    if (midW < 30) continue;

                    using var frameMidRoi = new Mat(gray, new OpenCvSharp.Rect(left + (int)(midW * 0.15), 0, (int)(midW * 0.70), scanMat.Height));
                    using var colMeanY = new Mat();
                    Cv2.Reduce(frameMidRoi, colMeanY, ReduceDimension.Column, ReduceTypes.Avg, MatType.CV_32F);
                    float[] colValsY = new float[scanMat.Height];
                    System.Runtime.InteropServices.Marshal.Copy(colMeanY.Data, colValsY, 0, scanMat.Height);

                    int topMin = Math.Max(0, r.Y - searchWinY);
                    int topMax = Math.Min(scanMat.Height, r.Y + searchWinY);
                    float maxTopGrad = 0;
                    int detectedTopY = r.Y;
                    for (int k = topMin + 1; k < topMax - 1; k++)
                    {
                        float diff = Math.Abs(colValsY[k + 1] - colValsY[k - 1]);
                        if (diff > maxTopGrad) { maxTopGrad = diff; detectedTopY = k; }
                    }

                    int botMin = Math.Max(0, (r.Y + r.Height) - searchWinY);
                    int botMax = Math.Min(scanMat.Height, (r.Y + r.Height) + searchWinY);
                    float maxBotGrad = 0;
                    int detectedBotY = r.Y + r.Height;
                    for (int k = botMin + 1; k < botMax - 1; k++)
                    {
                        float diff = Math.Abs(colValsY[k + 1] - colValsY[k - 1]);
                        if (diff > maxBotGrad) { maxBotGrad = diff; detectedBotY = k; }
                    }

                    if (maxTopGrad >= p.MinEdgeGradientThreshold && maxBotGrad >= p.MinEdgeGradientThreshold)
                    {
                        int span = detectedBotY - detectedTopY;
                        if (Math.Abs(span - targetH) <= targetH * p.DimensionTolerance)
                        {
                            yOffsets.Add(detectedTopY);
                        }
                    }
                }

                if (yOffsets.Count > 0)
                {
                    yOffsets.Sort();
                    bestY = yOffsets[yOffsets.Count / 2];
                }

                int prevRight = -1;
                for (int i = 0; i < baseFrames.Count; i++)
                {
                    var (left, right, _) = refinedRangesX[i];
                    if (prevRight >= 0 && left < prevRight + 2)
                    {
                        left = prevRight + 2;
                    }
                    if (right <= left + 20)
                    {
                        right = left + targetW;
                    }

                    int clampedLeft = Math.Max(0, Math.Min(left, scanMat.Width - 10));
                    int clampedRight = Math.Min(scanMat.Width, Math.Max(clampedLeft + 10, right));
                    int clampedY = Math.Max(0, Math.Min(bestY, scanMat.Height - bestH));
                    int clampedH = Math.Min(bestH, scanMat.Height - clampedY);
                    int clampedW = clampedRight - clampedLeft;

                    refinedList.Add(new OpenCvSharp.Rect(clampedLeft, clampedY, clampedW, clampedH));
                    prevRight = clampedRight;
                }
            }

            return refinedList;
        }

        /// <summary>
        /// 検出されたコマ矩形リストに基づき、画像から各コマを安全に切り出す
        /// </summary>
        public List<Mat> CropFrames(Mat straightenedMat, List<OpenCvSharp.Rect> frames)
        {
            var crops = new List<Mat>();
            if (straightenedMat == null || straightenedMat.Empty() || frames == null) return crops;

            foreach (var r in frames)
            {
                int x = Math.Max(0, Math.Min(r.X, straightenedMat.Width - 1));
                int y = Math.Max(0, Math.Min(r.Y, straightenedMat.Height - 1));
                int w = Math.Max(1, Math.Min(r.Width, straightenedMat.Width - x));
                int h = Math.Max(1, Math.Min(r.Height, straightenedMat.Height - y));

                using var roi = new Mat(straightenedMat, new OpenCvSharp.Rect(x, y, w, h));
                crops.Add(roi.Clone());
            }

            return crops;
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
