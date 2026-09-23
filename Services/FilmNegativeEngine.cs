using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using IrisPxS.Models;

namespace IrisPxS.Services
{
    public class FilmNegativeEngine
    {
        public FilmNegativeEngine()
        {
        }

        /// <summary>
        /// フィルム画像からベースカラー（未露光オレンジマスク色）を高精度に自動検知する
        /// ネガフィルムにおいて、コマ間やパーフォレーション周辺などの未露光部は最も光を透過し、最も明るいオレンジ色となる
        /// スキャナーガラスの素抜け白飛び（純白）や被写体の影響を完全に排除してサンプリングします
        /// </summary>
        public (byte R, byte G, byte B) DetectBaseColor(Mat rawMat, OpenCvSharp.Rect? cropRect = null)
        {
            if (rawMat.Empty()) return (215, 125, 75);

            using var sampleMat = new Mat();
            if (cropRect.HasValue && cropRect.Value.Width > 20 && cropRect.Value.Height > 20)
            {
                var rect = cropRect.Value;
                // コマの外周（マージン部）を周囲15%分広くサンプリング
                int marginX = Math.Max(10, (int)(rect.Width * 0.15));
                int marginY = Math.Max(10, (int)(rect.Height * 0.15));

                int sx = Math.Max(0, rect.X - marginX);
                int sy = Math.Max(0, rect.Y - marginY);
                int sw = Math.Min(rawMat.Width - sx, rect.Width + marginX * 2);
                int sh = Math.Min(rawMat.Height - sy, rect.Height + marginY * 2);

                using var sub = new Mat(rawMat, new OpenCvSharp.Rect(sx, sy, sw, sh));
                Cv2.Resize(sub, sampleMat, new OpenCvSharp.Size(Math.Min(400, sw), Math.Min(400, sh)));
            }
            else
            {
                Cv2.Resize(rawMat, sampleMat, new OpenCvSharp.Size(400, (int)(400.0 * rawMat.Height / rawMat.Width)));
            }

            // BGRチャンネル分割
            var channels = Cv2.Split(sampleMat);
            using var bChan = channels[0];
            using var gChan = channels[1];
            using var rChan = channels[2];

            // オレンジマスク画素を厳密に抽出する条件マスクを作成
            // 1. 純白（スキャナーのガラス素抜け 245以上）を除外
            // 2. 純黒（ホルダー枠 35以下）を除外
            // 3. オレンジ色の特性: R > G > B かつ (R - G >= 15) かつ (G - B >= 10)
            using var mask = new Mat(sampleMat.Size(), MatType.CV_8UC1, new Scalar(0));

            int rows = sampleMat.Rows;
            int cols = sampleMat.Cols;
            var orangePixels = new List<(byte R, byte G, byte B, int Brightness)>();

            for (int y = 0; y < rows; y += 2) // 高速化のため2画素おき
            {
                for (int x = 0; x < cols; x += 2)
                {
                    var vec = sampleMat.At<Vec3b>(y, x);
                    byte b = vec.Item0;
                    byte g = vec.Item1;
                    byte r = vec.Item2;

                    // 白飛び（素抜けガラス）除外
                    if (r >= 245 && g >= 245 && b >= 245) continue;
                    // 黒（ホルダー枠）除外
                    if (r <= 35 && g <= 35 && b <= 35) continue;

                    // オレンジマスクの基本比率
                    if (r > g && g > b && (r - g >= 12) && (g - b >= 8) && r >= 100)
                    {
                        int brightness = (int)(r * 0.299 + g * 0.587 + b * 0.114);
                        orangePixels.Add((r, g, b, brightness));
                    }
                }
            }

            if (orangePixels.Count >= 20)
            {
                // 明るさ降順でソート（未露光部＝最も明るいオレンジ）
                orangePixels.Sort((p1, p2) => p2.Brightness.CompareTo(p1.Brightness));

                // 最も明るい上位10%〜30%の画素をサンプリング（極端な外れ値を除く）
                int takeCount = Math.Max(10, (int)(orangePixels.Count * 0.20));
                var topSamples = orangePixels.Take(takeCount).ToList();

                long sumR = 0, sumG = 0, sumB = 0;
                foreach (var p in topSamples)
                {
                    sumR += p.R;
                    sumG += p.G;
                    sumB += p.B;
                }

                byte finalR = (byte)Math.Clamp(sumR / takeCount, 80, 245);
                byte finalG = (byte)Math.Clamp(sumG / takeCount, 40, 220);
                byte finalB = (byte)Math.Clamp(sumB / takeCount, 20, 180);

                return (finalR, finalG, finalB);
            }

            // オレンジマスクが見つからなかった場合（モノクロフィルムやポジフィルムの場合など）
            return (215, 125, 75);
        }

        /// <summary>
        /// 生スキャン画像をポジ・補正画像に変換（Color/B&W, Negative/Positive 対応）
        /// プロファイル機能を排し、ピュアな物理ベース反転と精密トーンコントロールを行います
        /// </summary>
        public Mat ConvertNegativeToPositive(Mat rawMat, FilmFrame frameSettings)
        {
            if (rawMat.Empty()) return new Mat();

            bool isColor = frameSettings.IsColor;
            bool isNegative = frameSettings.IsNegative;

            // 1. カラーネガフィルムの場合 (Color + Negative)
            if (isColor && isNegative)
            {
                return ProcessColorNegative(rawMat, frameSettings);
            }
            // 2. モノクロネガフィルムの場合 (B&W + Negative)
            else if (!isColor && isNegative)
            {
                return ProcessBwNegative(rawMat, frameSettings);
            }
            // 3. カラーポジフィルムの場合 (Color + Positive)
            else if (isColor && !isNegative)
            {
                return ProcessPositive(rawMat, frameSettings, toGrayscale: false);
            }
            // 4. モノクロポジフィルムの場合 (B&W + Positive)
            else
            {
                return ProcessPositive(rawMat, frameSettings, toGrayscale: true);
            }
        }

        private Mat ProcessColorNegative(Mat rawMat, FilmFrame settings)
        {
            using var floatMat = new Mat();
            rawMat.ConvertTo(floatMat, MatType.CV_32FC3, 1.0 / 255.0);

            var channels = Cv2.Split(floatMat);
            var bChan = channels[0];
            var gChan = channels[1];
            var rChan = channels[2];

            // フィルムベースカラー正規化値 (0.05 ~ 1.0)
            float baseR = Math.Max(0.05f, settings.BaseColorR / 255.0f);
            float baseG = Math.Max(0.05f, settings.BaseColorG / 255.0f);
            float baseB = Math.Max(0.05f, settings.BaseColorB / 255.0f);

            // 1. ベースカラー減算・透過率正規化
            Cv2.Divide(rChan, new Scalar(baseR), rChan);
            Cv2.Divide(gChan, new Scalar(baseG), gChan);
            Cv2.Divide(bChan, new Scalar(baseB), bChan);

            Cv2.Min(rChan, new Scalar(1.0), rChan);
            Cv2.Min(gChan, new Scalar(1.0), gChan);
            Cv2.Min(bChan, new Scalar(1.0), bChan);

            // 2. ネガ反転 (Pixel = 1.0 - Pixel)
            using var ones = Mat.Ones(floatMat.Size(), MatType.CV_32FC1);
            Cv2.Subtract(ones, rChan, rChan);
            Cv2.Subtract(ones, gChan, gChan);
            Cv2.Subtract(ones, bChan, bChan);

            // 3. フィルムの標準ガンマカーブ (ガンマ約 1.8 〜 2.0 で素直な階調に伸長)
            float gamma = 1.8f;
            Cv2.Pow(rChan, gamma, rChan);
            Cv2.Pow(gChan, gamma, gChan);
            Cv2.Pow(bChan, gamma, bChan);

            // 4. 露出補正 (EV: 2^EV)
            float exposureMult = (float)Math.Pow(2.0, settings.Exposure);
            Cv2.Multiply(rChan, new Scalar(exposureMult), rChan);
            Cv2.Multiply(gChan, new Scalar(exposureMult), gChan);
            Cv2.Multiply(bChan, new Scalar(exposureMult), bChan);

            // 5. 色温度・色合い (ホワイトバランス微調整)
            float tempR = 1.0f + (float)settings.ColorTemp * 0.005f;
            float tempB = 1.0f - (float)settings.ColorTemp * 0.005f;
            float tintG = 1.0f + (float)settings.Tint * 0.005f;
            Cv2.Multiply(rChan, new Scalar(Math.Max(0.1f, tempR)), rChan);
            Cv2.Multiply(gChan, new Scalar(Math.Max(0.1f, tintG)), gChan);
            Cv2.Multiply(bChan, new Scalar(Math.Max(0.1f, tempB)), bChan);

            // 6. コントラスト補正
            float contrast = (float)settings.Contrast;
            if (Math.Abs(contrast - 1.0f) > 0.01f)
            {
                Cv2.Subtract(rChan, new Scalar(0.5), rChan);
                Cv2.Multiply(rChan, new Scalar(contrast), rChan);
                Cv2.Add(rChan, new Scalar(0.5), rChan);

                Cv2.Subtract(gChan, new Scalar(0.5), gChan);
                Cv2.Multiply(gChan, new Scalar(contrast), gChan);
                Cv2.Add(gChan, new Scalar(0.5), gChan);

                Cv2.Subtract(bChan, new Scalar(0.5), bChan);
                Cv2.Multiply(bChan, new Scalar(contrast), bChan);
                Cv2.Add(bChan, new Scalar(0.5), bChan);
            }

            // クリッピング (0.0 ~ 1.0)
            Cv2.Max(rChan, new Scalar(0.0), rChan);
            Cv2.Min(rChan, new Scalar(1.0), rChan);
            Cv2.Max(gChan, new Scalar(0.0), gChan);
            Cv2.Min(gChan, new Scalar(1.0), gChan);
            Cv2.Max(bChan, new Scalar(0.0), bChan);
            Cv2.Min(bChan, new Scalar(1.0), bChan);

            using var mergedFloat = new Mat();
            Cv2.Merge(new[] { bChan, gChan, rChan }, mergedFloat);

            foreach (var ch in channels) ch.Dispose();

            var result8u = new Mat();
            mergedFloat.ConvertTo(result8u, MatType.CV_8UC3, 255.0);

            // 7. 彩度補正
            if (Math.Abs(settings.Saturation - 1.0) > 0.01)
            {
                using var hsv = new Mat();
                Cv2.CvtColor(result8u, hsv, ColorConversionCodes.BGR2HSV);
                var hsvChans = Cv2.Split(hsv);
                hsvChans[1].ConvertTo(hsvChans[1], -1, settings.Saturation, 0);
                Cv2.Merge(hsvChans, hsv);
                Cv2.CvtColor(hsv, result8u, ColorConversionCodes.HSV2BGR);
                foreach (var c in hsvChans) c.Dispose();
            }

            return result8u;
        }

        private Mat ProcessBwNegative(Mat rawMat, FilmFrame settings)
        {
            using var gray = new Mat();
            if (rawMat.Channels() > 1)
            {
                Cv2.CvtColor(rawMat, gray, ColorConversionCodes.BGR2GRAY);
            }
            else
            {
                rawMat.CopyTo(gray);
            }

            // 浮動小数点変換
            using var floatGray = new Mat();
            gray.ConvertTo(floatGray, MatType.CV_32FC1, 1.0 / 255.0);

            // ネガ反転
            using var ones = Mat.Ones(floatGray.Size(), MatType.CV_32FC1);
            Cv2.Subtract(ones, floatGray, floatGray);

            // ガンマ・露出・コントラスト補正
            float gamma = 1.6f;
            Cv2.Pow(floatGray, gamma, floatGray);

            float exposureMult = (float)Math.Pow(2.0, settings.Exposure);
            Cv2.Multiply(floatGray, new Scalar(exposureMult), floatGray);

            float contrast = (float)settings.Contrast;
            if (Math.Abs(contrast - 1.0f) > 0.01f)
            {
                Cv2.Subtract(floatGray, new Scalar(0.5), floatGray);
                Cv2.Multiply(floatGray, new Scalar(contrast), floatGray);
                Cv2.Add(floatGray, new Scalar(0.5), floatGray);
            }

            Cv2.Max(floatGray, new Scalar(0.0), floatGray);
            Cv2.Min(floatGray, new Scalar(1.0), floatGray);

            using var resultGray8u = new Mat();
            floatGray.ConvertTo(resultGray8u, MatType.CV_8UC1, 255.0);

            // 3チャンネルBGRに展開
            var resultBgr = new Mat();
            Cv2.CvtColor(resultGray8u, resultBgr, ColorConversionCodes.GRAY2BGR);
            return resultBgr;
        }

        private Mat ProcessPositive(Mat rawMat, FilmFrame settings, bool toGrayscale)
        {
            var workMat = rawMat.Clone();

            if (toGrayscale)
            {
                using var gray = new Mat();
                Cv2.CvtColor(workMat, gray, ColorConversionCodes.BGR2GRAY);
                Cv2.CvtColor(gray, workMat, ColorConversionCodes.GRAY2BGR);
            }

            using var floatMat = new Mat();
            workMat.ConvertTo(floatMat, MatType.CV_32FC3, 1.0 / 255.0);
            workMat.Dispose();

            // 露出
            float exposureMult = (float)Math.Pow(2.0, settings.Exposure);
            Cv2.Multiply(floatMat, new Scalar(exposureMult, exposureMult, exposureMult), floatMat);

            // コントラスト
            float contrast = (float)settings.Contrast;
            if (Math.Abs(contrast - 1.0f) > 0.01f)
            {
                Cv2.Subtract(floatMat, new Scalar(0.5, 0.5, 0.5), floatMat);
                Cv2.Multiply(floatMat, new Scalar(contrast, contrast, contrast), floatMat);
                Cv2.Add(floatMat, new Scalar(0.5, 0.5, 0.5), floatMat);
            }

            Cv2.Max(floatMat, new Scalar(0.0), floatMat);
            Cv2.Min(floatMat, new Scalar(1.0), floatMat);

            var result8u = new Mat();
            floatMat.ConvertTo(result8u, MatType.CV_8UC3, 255.0);

            if (!toGrayscale && Math.Abs(settings.Saturation - 1.0) > 0.01)
            {
                using var hsv = new Mat();
                Cv2.CvtColor(result8u, hsv, ColorConversionCodes.BGR2HSV);
                var hsvChans = Cv2.Split(hsv);
                hsvChans[1].ConvertTo(hsvChans[1], -1, settings.Saturation, 0);
                Cv2.Merge(hsvChans, hsv);
                Cv2.CvtColor(hsv, result8u, ColorConversionCodes.HSV2BGR);
                foreach (var c in hsvChans) c.Dispose();
            }

            return result8u;
        }
    }
}
