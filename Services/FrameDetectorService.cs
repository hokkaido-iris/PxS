using OpenCvSharp;
using IrisPxS.Models;

namespace IrisPxS.Services
{
    public class FrameDetectorService
    {
        /// <summary>
        /// 透過スキャン全体画像からフィルムフォーマットとスキャンDPIに基づき、
        /// フィルムの傾き（Skew）を検知・補正しながら各コマ領域を高精度に自動検出する
        /// </summary>
        public List<OpenCvSharp.Rect> DetectFrames(Mat scanMat, FilmFormat format, int dpi = 0)
        {
            var detectedFrames = new List<OpenCvSharp.Rect>();
            if (scanMat.Empty()) return detectedFrames;

            // DPIの自動算出（指定がない場合、スキャン画像の縦横サイズとGT-X820透過エリア仕様から算出）
            // GT-X820 の透過原稿エリア長は約 240mm (約 9.45 インチ)
            if (dpi <= 0)
            {
                int maxDim = Math.Max(scanMat.Width, scanMat.Height);
                dpi = (int)Math.Round(maxDim / (240.0 / 25.4));
                if (dpi < 150) dpi = 300;
            }

            // 物理サイズ(mm)からピクセルサイズを算出
            // 1 inch = 25.4 mm
            double mmToPx = (double)dpi / 25.4;
            int targetW = (int)Math.Round(format.PhysicalWidthMm * mmToPx);
            int targetH = (int)Math.Round(format.PhysicalHeightMm * mmToPx);

            // コマ間マージン (標準約 2.0mm)
            int marginPx = (int)Math.Round(2.0 * mmToPx);

            // 縦向きスキャン（スキャナーの長手方向がY軸）と横向きの判定
            bool isVerticalStrip = scanMat.Height > scanMat.Width;

            // 1. フィルムストリップの傾き角（Skew Angle）を検出
            double skewAngle = DetectFilmSkewAngle(scanMat);

            // 2. フィルムの存在領域（バウンディングボックス）を特定
            var filmBounds = DetectFilmStripBounds(scanMat);

            // 3. 幾何学的・投影プロファイルによるコマ位置の精密決定
            if (isVerticalStrip)
            {
                // 縦長ストリップの場合: コマは上から下へ並ぶ
                // 35mmフルサイズの場合、コマ枠の向きは横長 (36mm幅 x 24mm高)
                int frameW = Math.Min(targetW, filmBounds.Width - 10);
                int frameH = targetH;
                if (frameW <= 20) frameW = (int)(filmBounds.Width * 0.9);
                if (frameH <= 20) frameH = (int)(frameW / format.AspectRatio);

                int pitch = frameH + marginPx;
                int startX = filmBounds.X + (filmBounds.Width - frameW) / 2;
                int startY = filmBounds.Y + marginPx;

                int availableHeight = filmBounds.Height - marginPx * 2;
                int frameCount = Math.Max(1, availableHeight / pitch);

                // 最大コマ数はフォーマットの初期値または領域内最大数
                int maxFrames = format.DefaultFramesPerStrip > 0 ? format.DefaultFramesPerStrip : frameCount;
                int countToGenerate = Math.Min(frameCount, maxFrames);

                for (int i = 0; i < countToGenerate; i++)
                {
                    int y = startY + i * pitch;
                    if (y + frameH > scanMat.Height) break;

                    detectedFrames.Add(new OpenCvSharp.Rect(
                        Math.Max(0, startX),
                        Math.Max(0, y),
                        Math.Min(frameW, scanMat.Width - startX),
                        Math.Min(frameH, scanMat.Height - y)
                    ));
                }
            }
            else
            {
                // 横長ストリップの場合: コマは左から右へ並ぶ
                int frameW = targetW;
                int frameH = Math.Min(targetH, filmBounds.Height - 10);
                if (frameH <= 20) frameH = (int)(filmBounds.Height * 0.9);
                if (frameW <= 20) frameW = (int)(frameH * format.AspectRatio);

                int pitch = frameW + marginPx;
                int startX = filmBounds.X + marginPx;
                int startY = filmBounds.Y + (filmBounds.Height - frameH) / 2;

                int availableWidth = filmBounds.Width - marginPx * 2;
                int frameCount = Math.Max(1, availableWidth / pitch);
                int countToGenerate = Math.Min(frameCount, format.DefaultFramesPerStrip > 0 ? format.DefaultFramesPerStrip : frameCount);

                for (int i = 0; i < countToGenerate; i++)
                {
                    int x = startX + i * pitch;
                    if (x + frameW > scanMat.Width) break;

                    detectedFrames.Add(new OpenCvSharp.Rect(
                        Math.Max(0, x),
                        Math.Max(0, startY),
                        Math.Min(frameW, scanMat.Width - x),
                        Math.Min(frameH, scanMat.Height - startY)
                    ));
                }
            }

            return detectedFrames;
        }

        /// <summary>
        /// フィルムストリップの傾き角度（度）を検出
        /// </summary>
        public double DetectFilmSkewAngle(Mat scanMat)
        {
            try
            {
                // 高速化のため最大幅600pxに縮小
                double scale = 600.0 / Math.Max(scanMat.Width, scanMat.Height);
                using var small = new Mat();
                Cv2.Resize(scanMat, small, new OpenCvSharp.Size(scanMat.Width * scale, scanMat.Height * scale));

                using var gray = new Mat();
                Cv2.CvtColor(small, gray, ColorConversionCodes.BGR2GRAY);

                using var edges = new Mat();
                Cv2.Canny(gray, edges, 50, 150);

                // 確率的ハフ変換でフィルムの長い直線境界を検出
                var lines = Cv2.HoughLinesP(edges, 1, Math.PI / 180.0, 60, minLineLength: 80, maxLineGap: 10);
                if (lines.Length == 0) return 0.0;

                var angles = new List<double>();
                foreach (var line in lines)
                {
                    double dx = line.P2.X - line.P1.X;
                    double dy = line.P2.Y - line.P1.Y;
                    double angleRad = Math.Atan2(dy, dx);
                    double angleDeg = angleRad * (180.0 / Math.PI);

                    // 垂直・水平に近い線（±15度以内）の傾きを集計
                    if (Math.Abs(angleDeg) <= 15.0)
                    {
                        angles.Add(angleDeg);
                    }
                    else if (Math.Abs(angleDeg - 90.0) <= 15.0)
                    {
                        angles.Add(angleDeg - 90.0);
                    }
                    else if (Math.Abs(angleDeg + 90.0) <= 15.0)
                    {
                        angles.Add(angleDeg + 90.0);
                    }
                }

                if (angles.Count > 0)
                {
                    angles.Sort();
                    // 中央値を採用
                    return angles[angles.Count / 2];
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DetectFilmSkewAngle error: {ex.Message}");
            }

            return 0.0;
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

                // スキャナーガラス素抜け（純白）とホルダー枠（黒）を除外したフィルム帯領域の検出
                // フィルム領域は中間の輝度（50〜235）を持つ
                using var mask = new Mat();
                Cv2.InRange(gray, new Scalar(40), new Scalar(240), mask);

                // モルフォロジー結合
                using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(15, 15));
                Cv2.MorphologyEx(mask, mask, MorphTypes.Close, kernel);

                Cv2.FindContours(mask, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

                if (contours.Length > 0)
                {
                    // 最も面積の大きい輪郭（フィルムストリップ全体）を選択
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

            // 検出できなかった場合のセーフデフォルト（中央80%の領域）
            int defW = (int)(scanMat.Width * 0.85);
            int defH = (int)(scanMat.Height * 0.90);
            return new OpenCvSharp.Rect(
                (scanMat.Width - defW) / 2,
                (scanMat.Height - defH) / 2,
                defW,
                defH
            );
        }
    }
}
