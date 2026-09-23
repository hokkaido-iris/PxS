using OpenCvSharp;
using IrisPxS.Models;

namespace IrisPxS.Services
{
    public class FrameDetectorService
    {
        /// <summary>
        /// フィルム全体のコントラストから斜めの傾き角（Skew Angle）を検出し、
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

            // 1. フィルム全体のコントラストから斜めの傾きを検出 (精度 0.1度)
            double skewAngle = DetectFilmSkewAngleFromContrast(scanMat);

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
        /// フィルム全体のコントラスト（濃度勾配・エッジエネルギー）から斜めの傾き角（度）を精密に検出
        /// </summary>
        public double DetectFilmSkewAngleFromContrast(Mat scanMat)
        {
            try
            {
                // 高速かつ大域的なコントラスト解析のため、最大長辺 800px に縮小
                double maxDim = Math.Max(scanMat.Width, scanMat.Height);
                double scale = 800.0 / maxDim;
                int smallW = Math.Max(10, (int)(scanMat.Width * scale));
                int smallH = Math.Max(10, (int)(scanMat.Height * scale));

                using var small = new Mat();
                Cv2.Resize(scanMat, small, new OpenCvSharp.Size(smallW, smallH), 0, 0, InterpolationFlags.Area);

                using var gray = new Mat();
                Cv2.CvtColor(small, gray, ColorConversionCodes.BGR2GRAY);

                // コントラスト勾配の計算 (Sobel X, Y)
                using var gradX = new Mat();
                using var gradY = new Mat();
                Cv2.Sobel(gray, gradX, MatType.CV_32F, 1, 0, 3);
                Cv2.Sobel(gray, gradY, MatType.CV_32F, 0, 1, 3);

                // 勾配強度（コントラスト差）と勾配角度の集計
                // フィルムの長辺・短辺・パーフォレーション・コマ境界面はすべて同一の傾き角度を持つため、
                // 全体のコントラスト勾配を集計することで被写体ノイズに極めて強い角度推定を行う
                int histBins = 401; // -20.0度 〜 +20.0度 (0.1度刻み)
                double[] angleHistogram = new double[histBins];
                double minAngle = -20.0;
                double maxAngle = 20.0;
                double step = 0.1;

                bool isVertical = smallH > smallW;

                float[] gxArr = new float[smallW * smallH];
                float[] gyArr = new float[smallW * smallH];
                System.Runtime.InteropServices.Marshal.Copy(gradX.Data, gxArr, 0, gxArr.Length);
                System.Runtime.InteropServices.Marshal.Copy(gradY.Data, gyArr, 0, gyArr.Length);

                int totalPixels = gxArr.Length;

                for (int i = 0; i < totalPixels; i++)
                {
                    float gx = gxArr[i];
                    float gy = gyArr[i];
                    float mag = (float)Math.Sqrt(gx * gx + gy * gy);

                    // 高コントラストなエッジ部（フィルム境界・コマ枠・ホルダー境界）のみを対象とする
                    if (mag < 30.0f) continue;

                    // 勾配角度（度）: -180 ~ +180
                    double rad = Math.Atan2(gy, gx);
                    double deg = rad * (180.0 / Math.PI);

                    // エッジ接線方向 = 勾配法線 + 90度
                    double tangentDeg = deg + 90.0;
                    while (tangentDeg > 90.0) tangentDeg -= 180.0;
                    while (tangentDeg < -90.0) tangentDeg += 180.0;

                    // 縦ストリップの場合、主軸は90度付近 (または -90度付近)
                    // 横ストリップの場合、主軸は0度付近
                    double devAngle;
                    if (isVertical)
                    {
                        // 縦軸（90度）からの傾き
                        if (tangentDeg > 0)
                            devAngle = tangentDeg - 90.0;
                        else
                            devAngle = tangentDeg + 90.0;
                    }
                    else
                    {
                        // 横軸（0度）からの傾き
                        devAngle = tangentDeg;
                    }

                    if (devAngle >= minAngle && devAngle <= maxAngle)
                    {
                        int bin = (int)Math.Round((devAngle - minAngle) / step);
                        if (bin >= 0 && bin < histBins)
                        {
                            angleHistogram[bin] += mag; // コントラスト強度で重み付け
                        }
                    }
                }

                // ヒストグラムの最大ピーク探索
                int bestBin = -1;
                double maxWeight = 0;
                for (int b = 0; b < histBins; b++)
                {
                    if (angleHistogram[b] > maxWeight)
                    {
                        maxWeight = angleHistogram[b];
                        bestBin = b;
                    }
                }

                double coarseAngle = 0.0;
                if (bestBin >= 0 && maxWeight > 500.0)
                {
                    coarseAngle = minAngle + bestBin * step;
                }

                // 投影プロファイルコントラスト（Projection Profile Variance）による精密検証
                // coarseAngle 周囲 ±1.5度を 0.1度刻みで探索し、エッジが最も直立・直角になるピークを確定
                double refinedAngle = RefineAngleByProjectionContrast(gray, isVertical, coarseAngle);
                return refinedAngle;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DetectFilmSkewAngleFromContrast error: {ex.Message}");
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

            double searchStart = Math.Max(-20.0, candidateAngle - 1.5);
            double searchEnd = Math.Min(20.0, candidateAngle + 1.5);

            for (double angle = searchStart; angle <= searchEnd; angle += 0.1)
            {
                using var rotMat = Cv2.GetRotationMatrix2D(new Point2f(gray.Width / 2f, gray.Height / 2f), -angle, 1.0);
                using var rotated = new Mat();
                Cv2.WarpAffine(gray, rotated, rotMat, gray.Size(), InterpolationFlags.Linear, BorderTypes.Replicate);

                // 投影プロファイルの計算
                // 縦ストリップの場合: 列ごとの投影（X軸）の微分自乗和（エッジの急峻度）をコントラストとする
                // 横ストリップの場合: 行ごとの投影（Y軸）の微分自乗和
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
        /// 正立された画像上でフィルムストリップ位置を特定し、フォーマット通りのコマ枠を配置
        /// </summary>
        private List<OpenCvSharp.Rect> DetectFramesOnStraightened(Mat scanMat, FilmFormat format, int dpi)
        {
            var frames = new List<OpenCvSharp.Rect>();
            bool isVertical = scanMat.Height > scanMat.Width;

            // フォーマットから厳密なサイズ・縦横比・ピッチを取得
            GetFormatDimensions(format, isVertical, dpi, out int targetW, out int targetH, out int pitchPx);

            // フィルム領域の特定
            var filmBounds = DetectFilmStripBounds(scanMat);

            if (isVertical)
            {
                // 縦ストリップ: 横方向(X)はフィルム帯の中央にぴったり配置
                int frameW = targetW;
                int frameH = targetH;

                int startX = filmBounds.X + (filmBounds.Width - frameW) / 2;
                if (startX < 0) startX = Math.Max(0, (scanMat.Width - frameW) / 2);
                if (startX + frameW > scanMat.Width) startX = Math.Max(0, scanMat.Width - frameW);

                // コマ開始Y位置の検出（フィルム帯の上端から）
                int startY = filmBounds.Y + (int)Math.Round(2.0 * dpi / 25.4); // 2mm余白
                if (startY < 0) startY = 10;

                int availableHeight = scanMat.Height - startY;
                int maxCount = Math.Max(1, availableHeight / pitchPx);
                int countToGenerate = format.DefaultFramesPerStrip > 0
                    ? Math.Min(format.DefaultFramesPerStrip, maxCount)
                    : maxCount;

                for (int i = 0; i < countToGenerate; i++)
                {
                    int y = startY + i * pitchPx;
                    if (y + frameH > scanMat.Height) break;

                    frames.Add(new OpenCvSharp.Rect(startX, y, frameW, frameH));
                }
            }
            else
            {
                // 横ストリップ: 縦方向(Y)はフィルム帯の中央に配置
                int frameW = targetW;
                int frameH = targetH;

                int startY = filmBounds.Y + (filmBounds.Height - frameH) / 2;
                if (startY < 0) startY = Math.Max(0, (scanMat.Height - frameH) / 2);
                if (startY + frameH > scanMat.Height) startY = Math.Max(0, scanMat.Height - frameH);

                int startX = filmBounds.X + (int)Math.Round(2.0 * dpi / 25.4);
                if (startX < 0) startX = 10;

                int availableWidth = scanMat.Width - startX;
                int maxCount = Math.Max(1, availableWidth / pitchPx);
                int countToGenerate = format.DefaultFramesPerStrip > 0
                    ? Math.Min(format.DefaultFramesPerStrip, maxCount)
                    : maxCount;

                for (int i = 0; i < countToGenerate; i++)
                {
                    int x = startX + i * pitchPx;
                    if (x + frameW > scanMat.Width) break;

                    frames.Add(new OpenCvSharp.Rect(x, startY, frameW, frameH));
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

                // スキャナーガラス白飛び(>240)とホルダー漆黒(<35)を除いたフィルム帯領域
                using var mask = new Mat();
                Cv2.InRange(gray, new Scalar(35), new Scalar(240), mask);

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
        /// 既存のシグネチャ互換用
        /// </summary>
        public List<OpenCvSharp.Rect> DetectFrames(Mat scanMat, FilmFormat format, int dpi = 0)
        {
            var (_, _, frames) = DetectAndStraighten(scanMat, format, dpi);
            return frames;
        }
    }
}
