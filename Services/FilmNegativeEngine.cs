using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using IrisPxS.Models;
using System.Windows.Media.Imaging;

namespace IrisPxS.Services
{
    public class FilmNegativeEngine
    {
        private readonly Dictionary<string, FilmProfile> _profiles;

        public FilmNegativeEngine()
        {
            _profiles = FilmProfile.GetPresetProfiles().ToDictionary(p => p.Id, p => p);
        }

        public FilmProfile GetProfile(string profileId)
        {
            if (_profiles.TryGetValue(profileId, out var profile))
                return profile;
            return _profiles["portra400"];
        }

        /// <summary>
        /// フィルム画像からベースカラー（未露光オレンジマスク色）を自動検知する
        /// ネガフィルムにおいて、コマ間やパーフォレーション周辺などの未露光部は最も光を透過し、最も明るいオレンジ色となる
        /// </summary>
        public (byte R, byte G, byte B) DetectBaseColor(Mat rawMat, OpenCvSharp.Rect? cropRect = null)
        {
            using var sampleMat = new Mat();
            if (cropRect.HasValue && cropRect.Value.Width > 0 && cropRect.Value.Height > 0)
            {
                // コマの外周（マージン部）をサンプリング
                var rect = cropRect.Value;
                // クロップ枠の外周10%をサンプリング領域とする
                int marginX = Math.Max(2, (int)(rect.Width * 0.05));
                int marginY = Math.Max(2, (int)(rect.Height * 0.05));

                var expanded = new OpenCvSharp.Rect(
                    Math.Max(0, rect.X - marginX),
                    Math.Max(0, rect.Y - marginY),
                    Math.Min(rawMat.Width - Math.Max(0, rect.X - marginX), rect.Width + marginX * 2),
                    Math.Min(rawMat.Height - Math.Max(0, rect.Y - marginY), rect.Height + marginY * 2)
                );
                rawMat[expanded].CopyTo(sampleMat);
            }
            else
            {
                // 画像全体をダウンサンプリングして解析
                Cv2.Resize(rawMat, sampleMat, new OpenCvSharp.Size(320, 240));
            }

            // BGRチャンネルに分割
            var channels = Cv2.Split(sampleMat);
            using var bChan = channels[0];
            using var gChan = channels[1];
            using var rChan = channels[2];

            // 輝度（明度）が高い画素（上位5%〜10%の領域）を未露光ベースとして特定
            using var gray = new Mat();
            Cv2.CvtColor(sampleMat, gray, ColorConversionCodes.BGR2GRAY);

            // 輝度ソート用ヒストグラム/パーセンタイル
            double minVal, maxVal;
            OpenCvSharp.Point minLoc, maxLoc;
            Cv2.MinMaxLoc(gray, out minVal, out maxVal, out minLoc, out maxLoc);

            // 最も明るい上位10%の画素を抽出する閾値
            double thresholdVal = maxVal * 0.90;
            using var mask = new Mat();
            Cv2.Threshold(gray, mask, thresholdVal, 255, ThresholdTypes.Binary);

            // マスク部分の平均BGRを取得
            var meanScalar = Cv2.Mean(sampleMat, mask);

            byte b = (byte)Math.Clamp(meanScalar.Val0, 20, 255);
            byte g = (byte)Math.Clamp(meanScalar.Val1, 40, 255);
            byte r = (byte)Math.Clamp(meanScalar.Val2, 80, 255);

            // オレンジマスクの基本特性: R > G > B
            // もし何らかの理由で極端な値になった場合の安全ガード
            if (r < g) r = (byte)Math.Min(255, g + 40);
            if (g < b) g = (byte)Math.Min(255, b + 30);

            return (r, g, b);
        }

        /// <summary>
        /// 生スキャンネガ画像をポジ画像に変換（NP変換）
        /// </summary>
        public Mat ConvertNegativeToPositive(Mat rawMat, FilmFrame frameSettings)
        {
            if (rawMat.Empty())
                return new Mat();

            var profile = GetProfile(frameSettings.ProfileId);

            // 32ビット浮動小数点に変換して高精度計算 (0.0 ~ 1.0)
            using var floatMat = new Mat();
            rawMat.ConvertTo(floatMat, MatType.CV_32FC3, 1.0 / 255.0);

            // チャンネル分割 (BGR)
            var channels = Cv2.Split(floatMat);
            var bChan = channels[0];
            var gChan = channels[1];
            var rChan = channels[2];

            // ベースカラーの正規化値 (0.0 ~ 1.0)
            float baseR = Math.Max(0.1f, frameSettings.BaseColorR / 255.0f);
            float baseG = Math.Max(0.1f, frameSettings.BaseColorG / 255.0f);
            float baseB = Math.Max(0.1f, frameSettings.BaseColorB / 255.0f);

            // 1. ベースカラー減算・正規化 (未露光ベースを純白/最大透過とする)
            Cv2.Divide(rChan, new Scalar(baseR), rChan);
            Cv2.Divide(gChan, new Scalar(baseG), gChan);
            Cv2.Divide(bChan, new Scalar(baseB), bChan);

            // 1.0を超えないようクリッピング
            Cv2.Min(rChan, new Scalar(1.0), rChan);
            Cv2.Min(gChan, new Scalar(1.0), gChan);
            Cv2.Min(bChan, new Scalar(1.0), bChan);

            // 2. 対数/線形反転 (Pixel_inv = 1.0 - Pixel)
            using var ones = Mat.Ones(floatMat.Size(), MatType.CV_32FC1);
            Cv2.Subtract(ones, rChan, rChan);
            Cv2.Subtract(ones, gChan, gChan);
            Cv2.Subtract(ones, bChan, bChan);

            // 3. フィルムプロファイルのガンマ補正適用
            float gammaR = (float)(profile.GammaR);
            float gammaG = (float)(profile.GammaG);
            float gammaB = (float)(profile.GammaB);

            // 露出補正 (EV: 2^EV)
            float exposureMult = (float)Math.Pow(2.0, frameSettings.Exposure);
            Cv2.Multiply(rChan, new Scalar(exposureMult), rChan);
            Cv2.Multiply(gChan, new Scalar(exposureMult), gChan);
            Cv2.Multiply(bChan, new Scalar(exposureMult), bChan);

            // ガンマ累乗 (Cv2.Pow)
            Cv2.Pow(rChan, 1.0 / gammaR, rChan);
            Cv2.Pow(gChan, 1.0 / gammaG, gChan);
            Cv2.Pow(bChan, 1.0 / gammaB, bChan);

            // 4. ホワイトバランス（色温度/色被り）調整
            if (Math.Abs(frameSettings.ColorTemp) > 0.01)
            {
                // ColorTemp > 0: 暖色 (R増, B減), < 0: 寒色 (B増, R減)
                float tempFactor = (float)(frameSettings.ColorTemp / 100.0);
                Cv2.Multiply(rChan, new Scalar(1.0 + tempFactor), rChan);
                Cv2.Multiply(bChan, new Scalar(1.0 - tempFactor), bChan);
            }
            if (Math.Abs(frameSettings.Tint) > 0.01)
            {
                // Tint > 0: マゼンタ (G減), < 0: グリーン (G増)
                float tintFactor = (float)(frameSettings.Tint / 100.0);
                Cv2.Multiply(gChan, new Scalar(1.0 - tintFactor), gChan);
            }

            // チャンネル統合
            var merged = new Mat();
            Cv2.Merge(new Mat[] { bChan, gChan, rChan }, merged);

            // 解放
            foreach (var ch in channels) ch.Dispose();

            // 5. コントラスト調整 (S字カーブまたは線形コントラスト)
            float contrast = (float)(profile.Contrast * frameSettings.Contrast);
            if (Math.Abs(contrast - 1.0f) > 0.01)
            {
                // (Pixel - 0.5) * contrast + 0.5
                Cv2.Subtract(merged, new Scalar(0.5, 0.5, 0.5), merged);
                Cv2.Multiply(merged, new Scalar(contrast, contrast, contrast), merged);
                Cv2.Add(merged, new Scalar(0.5, 0.5, 0.5), merged);
            }

            // 0.0 ~ 1.0 にクリッピング
            Cv2.Max(merged, new Scalar(0.0, 0.0, 0.0), merged);
            Cv2.Min(merged, new Scalar(1.0, 1.0, 1.0), merged);

            // 8ビット (0~255) に変換
            var result8u = new Mat();
            merged.ConvertTo(result8u, MatType.CV_8UC3, 255.0);
            merged.Dispose();

            // 6. 彩度調整 (HSV空間)
            float totalSat = (float)(profile.SaturationMultiplier * frameSettings.Saturation);
            if (profile.IsMonochrome || totalSat < 0.01f)
            {
                // モノクロ変換
                using var gray = new Mat();
                Cv2.CvtColor(result8u, gray, ColorConversionCodes.BGR2GRAY);
                Cv2.CvtColor(gray, result8u, ColorConversionCodes.GRAY2BGR);
            }
            else if (Math.Abs(totalSat - 1.0f) > 0.01f)
            {
                using var hsv = new Mat();
                Cv2.CvtColor(result8u, hsv, ColorConversionCodes.BGR2HSV);
                var hsvCh = Cv2.Split(hsv);
                hsvCh[1].ConvertTo(hsvCh[1], MatType.CV_32F);
                Cv2.Multiply(hsvCh[1], new Scalar(totalSat), hsvCh[1]);
                Cv2.Min(hsvCh[1], new Scalar(255.0), hsvCh[1]);
                hsvCh[1].ConvertTo(hsvCh[1], MatType.CV_8U);
                Cv2.Merge(hsvCh, hsv);
                Cv2.CvtColor(hsv, result8u, ColorConversionCodes.HSV2BGR);
                foreach (var ch in hsvCh) ch.Dispose();
            }

            return result8u;
        }

        /// <summary>
        /// MatをWPFのBitmapSourceに変換
        /// </summary>
        public BitmapSource MatToBitmapSource(Mat mat)
        {
            return mat.ToBitmapSource();
        }
    }
}
