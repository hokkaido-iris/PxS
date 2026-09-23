using OpenCvSharp;

namespace IrisPxS.Services
{
    public class DustScratchRemovalService
    {
        /// <summary>
        /// 赤外線(IR)スキャンデータからゴミ・傷マスクを生成
        /// カラーフィルムの染料は赤外線を透過するが、ホコリ・傷は赤外線を遮蔽または散乱する
        /// </summary>
        public Mat GenerateDefectMaskFromIr(Mat irMat, int strength = 2)
        {
            using var gray = new Mat();
            if (irMat.Channels() > 1)
            {
                Cv2.CvtColor(irMat, gray, ColorConversionCodes.BGR2GRAY);
            }
            else
            {
                irMat.CopyTo(gray);
            }

            // 赤外線の透過が遮られて暗くなっている箇所（または傷による急峻な高周波）を検出
            // 適応的閾値または大津の二値化
            var mask = new Mat();
            
            // 強度に応じた閾値調整
            double thresh = strength switch
            {
                1 => 45.0,  // 弱: 顕著なゴミのみ
                3 => 70.0,  // 強: 微細なチリまで
                _ => 55.0   // 標準
            };

            // 背景とホコリの差分（ローパスフィルタとの差）
            using var blur = new Mat();
            Cv2.GaussianBlur(gray, blur, new OpenCvSharp.Size(15, 15), 0);

            using var diff = new Mat();
            Cv2.Absdiff(blur, gray, diff);

            // 欠陥部分を二値化
            Cv2.Threshold(diff, mask, thresh, 255, ThresholdTypes.Binary);

            // モルフォロジー演算でノイズを整理し、傷・ゴミの輪郭を少し膨張させる
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new OpenCvSharp.Size(3, 3));
            Cv2.Dilate(mask, mask, kernel, iterations: 1);

            return mask;
        }

        /// <summary>
        /// 赤外線データがない場合（銀塩モノクロフィルム等）の画像解析によるゴミ・キズ自動検知
        /// </summary>
        public Mat GenerateDefectMaskFromColor(Mat colorMat, int strength = 2)
        {
            using var gray = new Mat();
            Cv2.CvtColor(colorMat, gray, ColorConversionCodes.BGR2GRAY);

            // メディアンフィルタと元画像の差分（孤立したインパルスノイズ・白いホコリや黒いチリ）
            using var median = new Mat();
            Cv2.MedianBlur(gray, median, 5);

            using var diff = new Mat();
            Cv2.Absdiff(gray, median, diff);

            var mask = new Mat();
            double thresh = strength switch
            {
                1 => 35.0,
                3 => 20.0,
                _ => 28.0
            };

            Cv2.Threshold(diff, mask, thresh, 255, ThresholdTypes.Binary);

            // あまりに大きい領域（被写体のエッジ）を除去するため、接続成分解析で面積制限
            using var labels = new Mat();
            using var stats = new Mat();
            using var centroids = new Mat();
            int nLabels = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids);

            var cleanMask = new Mat(mask.Size(), MatType.CV_8UC1, new Scalar(0));
            int maxArea = 250; // 大きすぎる塊は被写体の輪郭の可能性があるため除外

            for (int i = 1; i < nLabels; i++)
            {
                int area = stats.At<int>(i, (int)ConnectedComponentsTypes.Area);
                if (area > 2 && area < maxArea)
                {
                    // 有効なゴミとしてマスクに残す
                    using var componentMask = new Mat();
                    Cv2.Compare(labels, new Scalar(i), componentMask, CmpTypes.EQ);
                    Cv2.BitwiseOr(cleanMask, componentMask, cleanMask);
                }
            }

            return cleanMask;
        }

        /// <summary>
        /// 欠陥マスクを用いて画像のゴミ・キズをインペインティング修復する
        /// </summary>
        public Mat RemoveDustAndScratches(Mat srcMat, Mat defectMask, InpaintTypes method = InpaintTypes.Telea)
        {
            if (srcMat.Empty() || defectMask.Empty())
                return srcMat.Clone();

            var dst = new Mat();
            // InpaintRadius: 修復領域の参照半径
            double radius = 3.0;
            Cv2.Inpaint(srcMat, defectMask, dst, radius, method);
            return dst;
        }
    }
}
