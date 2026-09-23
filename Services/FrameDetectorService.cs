using OpenCvSharp;
using IrisPxS.Models;

namespace IrisPxS.Services
{
    public class FrameDetectorService
    {
        /// <summary>
        /// 透過スキャン全体画像からフィルムフォーマットに応じた各コマ領域を自動検出する
        /// </summary>
        public List<OpenCvSharp.Rect> DetectFrames(Mat scanMat, FilmFormat format)
        {
            var detectedFrames = new List<OpenCvSharp.Rect>();
            if (scanMat.Empty()) return detectedFrames;

            // 高速処理のため解析用縮小画像を作成
            double scale = 1.0;
            Mat procMat;
            int maxDim = 1600;
            if (Math.Max(scanMat.Width, scanMat.Height) > maxDim)
            {
                scale = (double)maxDim / Math.Max(scanMat.Width, scanMat.Height);
                procMat = new Mat();
                Cv2.Resize(scanMat, procMat, new OpenCvSharp.Size(scanMat.Width * scale, scanMat.Height * scale));
            }
            else
            {
                procMat = scanMat.Clone();
            }

            try
            {
                // グレースケール化
                using var gray = new Mat();
                Cv2.CvtColor(procMat, gray, ColorConversionCodes.BGR2GRAY);

                // フィルムストリップの外形とコマ枠を検出
                // 平滑化
                using var blurred = new Mat();
                Cv2.GaussianBlur(gray, blurred, new OpenCvSharp.Size(7, 7), 0);

                // エッジ検出
                using var edges = new Mat();
                Cv2.Canny(blurred, edges, 30, 100);

                // モルフォロジー演算で途切れたエッジを結合
                using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(9, 9));
                Cv2.MorphologyEx(edges, edges, MorphTypes.Close, kernel);

                // 輪郭抽出
                Cv2.FindContours(edges, out var contours, out _, RetrievalModes.Tree, ContourApproximationModes.ApproxSimple);

                double targetAspect = format.AspectRatio;
                double aspectTolerance = 0.35; // 許容ブレ幅

                // 画像全体の面積に対する最小・最大割合
                double totalArea = procMat.Width * procMat.Height;
                double minFrameArea = totalArea * 0.01;
                double maxFrameArea = totalArea * 0.50;

                var candidateRects = new List<OpenCvSharp.Rect>();

                foreach (var contour in contours)
                {
                    var rect = Cv2.BoundingRect(contour);
                    double area = rect.Width * rect.Height;

                    if (area >= minFrameArea && area <= maxFrameArea)
                    {
                        double aspect = (double)rect.Width / Math.Max(1, rect.Height);
                        // 横向きまたは縦向きでアスペクト比を比較
                        bool matchesAspect = Math.Abs(aspect - targetAspect) < aspectTolerance ||
                                             Math.Abs((1.0 / aspect) - targetAspect) < aspectTolerance;

                        if (matchesAspect)
                        {
                            candidateRects.Add(rect);
                        }
                    }
                }

                // 重複している矩形をマージ (Non-Maximum Suppression 的処理)
                var filtered = FilterOverlappingRects(candidateRects);

                // もし輪郭検出で十分なコマが見つからなかった場合（フィルムのベースが均一でエッジが薄い場合など）、
                // フィルムストリップ領域を推定して幾何学的に等分割グリッドを配置するフォールバック
                if (filtered.Count < 2)
                {
                    filtered = GenerateHeuristicGrid(procMat, format);
                }

                // スケールを元画像の解像度に戻す
                foreach (var r in filtered)
                {
                    int x = Math.Max(0, (int)(r.X / scale));
                    int y = Math.Max(0, (int)(r.Y / scale));
                    int w = Math.Min(scanMat.Width - x, (int)(r.Width / scale));
                    int h = Math.Min(scanMat.Height - y, (int)(r.Height / scale));

                    if (w > 20 && h > 20)
                    {
                        detectedFrames.Add(new OpenCvSharp.Rect(x, y, w, h));
                    }
                }

                // 左から右、上から下の順序でソート
                detectedFrames = SortFramesReadingOrder(detectedFrames);
            }
            finally
            {
                procMat.Dispose();
            }

            return detectedFrames;
        }

        private List<OpenCvSharp.Rect> FilterOverlappingRects(List<OpenCvSharp.Rect> rects)
        {
            var result = new List<OpenCvSharp.Rect>();
            // 面積の大きい順にソート
            var sorted = rects.OrderByDescending(r => r.Width * r.Height).ToList();

            foreach (var r in sorted)
            {
                bool isOverlap = false;
                foreach (var existing in result)
                {
                    var intersect = r.Intersect(existing);
                    if (intersect.Width > 0 && intersect.Height > 0)
                    {
                        double intersectArea = intersect.Width * intersect.Height;
                        double minArea = Math.Min(r.Width * r.Height, existing.Width * existing.Height);
                        if (intersectArea / minArea > 0.4) // 40%以上重なっていれば除外
                        {
                            isOverlap = true;
                            break;
                        }
                    }
                }
                if (!isOverlap)
                {
                    result.Add(r);
                }
            }

            return result;
        }

        private List<OpenCvSharp.Rect> GenerateHeuristicGrid(Mat procMat, FilmFormat format)
        {
            var list = new List<OpenCvSharp.Rect>();
            int nFrames = Math.Max(1, format.DefaultFramesPerStrip);

            // 中央の70%領域にストリップが存在すると仮定
            int stripW = (int)(procMat.Width * 0.85);
            int stripH = (int)(procMat.Height * 0.40);
            int startX = (procMat.Width - stripW) / 2;
            int startY = (procMat.Height - stripH) / 2;

            int frameGap = 15;
            int totalGap = frameGap * (nFrames - 1);
            int frameW = (stripW - totalGap) / nFrames;
            int frameH = (int)(frameW / format.AspectRatio);

            if (frameH > stripH)
            {
                frameH = stripH;
                frameW = (int)(frameH * format.AspectRatio);
            }

            for (int i = 0; i < nFrames; i++)
            {
                int x = startX + i * (frameW + frameGap);
                int y = startY + (stripH - frameH) / 2;
                list.Add(new OpenCvSharp.Rect(x, y, frameW, frameH));
            }

            return list;
        }

        private List<OpenCvSharp.Rect> SortFramesReadingOrder(List<OpenCvSharp.Rect> rects)
        {
            // Y座標でクラスタリング（行分け）し、各行の中でX座標順にソート
            return rects.OrderBy(r => r.Y / 100).ThenBy(r => r.X).ToList();
        }
    }
}
