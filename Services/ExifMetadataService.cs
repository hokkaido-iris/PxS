using System.IO;
using System.Windows.Media.Imaging;
using IrisPxS.Models;
using OpenCvSharp;

namespace IrisPxS.Services
{
    public class ExifMetadataService
    {
        /// <summary>
        /// 画像データにExifメタデータを埋め込んで保存する (JPEG / TIFF / PNG)
        /// </summary>
        public void SaveImageWithExif(
            Mat imageMat,
            FilmFrame frame,
            string outputPath,
            string format = "JPEG",
            int jpegQuality = 95)
        {
            // まずメモリストリームに一時エンコード
            byte[] encodedBytes;
            if (format.Equals("TIFF", StringComparison.OrdinalIgnoreCase))
            {
                Cv2.ImEncode(".tif", imageMat, out encodedBytes);
            }
            else if (format.Equals("PNG", StringComparison.OrdinalIgnoreCase))
            {
                Cv2.ImEncode(".png", imageMat, out encodedBytes);
            }
            else
            {
                var prms = new ImageEncodingParam(ImwriteFlags.JpegQuality, jpegQuality);
                Cv2.ImEncode(".jpg", imageMat, out encodedBytes, prms);
            }

            // WIC (BitmapMetadata) によるExif付与
            using var inStream = new MemoryStream(encodedBytes);
            BitmapDecoder decoder;
            try
            {
                decoder = BitmapDecoder.Create(inStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            }
            catch
            {
                // デコーダで開けない場合は直接保存
                File.WriteAllBytes(outputPath, encodedBytes);
                return;
            }

            var frameSource = decoder.Frames[0];
            var metadata = new BitmapMetadata(format.Equals("TIFF", StringComparison.OrdinalIgnoreCase) ? "tiff" : "jpg");

            try
            {
                // カメラ情報
                if (!string.IsNullOrWhiteSpace(frame.CameraMake))
                    metadata.SetQuery("/app1/ifd/{ushort=271}", frame.CameraMake); // Make

                if (!string.IsNullOrWhiteSpace(frame.CameraModel))
                    metadata.SetQuery("/app1/ifd/{ushort=272}", frame.CameraModel); // Model

                // レンズ情報
                if (!string.IsNullOrWhiteSpace(frame.LensModel))
                    metadata.SetQuery("/app1/ifd/exif/{ushort=42036}", frame.LensModel); // LensModel

                // F値 (絞り値) - EXIF では Rational
                if (frame.FNumber > 0)
                {
                    uint numerator = (uint)(frame.FNumber * 10);
                    uint denominator = 10;
                    ulong fNumberRational = ((ulong)numerator << 32) | denominator;
                    metadata.SetQuery("/app1/ifd/exif/{ushort=33437}", fNumberRational); // FNumber
                }

                // シャッター速度 (ExposureTime)
                if (TryParseShutterSpeed(frame.ShutterSpeed, out double exposureSec))
                {
                    uint num = 1;
                    uint den = (uint)Math.Max(1, Math.Round(1.0 / exposureSec));
                    if (exposureSec >= 1.0)
                    {
                        num = (uint)Math.Round(exposureSec);
                        den = 1;
                    }
                    ulong exposureRational = ((ulong)num << 32) | den;
                    metadata.SetQuery("/app1/ifd/exif/{ushort=33434}", exposureRational); // ExposureTime
                }

                // ISO感度
                if (frame.ISO > 0)
                {
                    metadata.SetQuery("/app1/ifd/exif/{ushort=34855}", (ushort)frame.ISO); // ISOSpeedRatings
                }

                // 焦点距離
                if (frame.FocalLength > 0)
                {
                    uint flNum = (uint)(frame.FocalLength * 10);
                    uint flDen = 10;
                    ulong flRational = ((ulong)flNum << 32) | flDen;
                    metadata.SetQuery("/app1/ifd/exif/{ushort=37386}", flRational); // FocalLength
                }

                // 撮影日時
                string dateStr = frame.DateTaken.ToString("yyyy:MM:dd HH:mm:ss");
                metadata.SetQuery("/app1/ifd/exif/{ushort=36867}", dateStr); // DateTimeOriginal

                // コメント・説明
                string comment = $"IRIS PxS Film Scan | Film: {frame.ProfileId} | Frame: #{frame.FrameNumber:D2}";
                if (!string.IsNullOrWhiteSpace(frame.Notes))
                {
                    comment += $" | Notes: {frame.Notes}";
                }
                metadata.SetQuery("/app1/ifd/exif/{ushort=37510}", comment); // UserComment
                metadata.SetQuery("/app1/ifd/{ushort=270}", comment);        // ImageDescription
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"EXIF Query Setting Notice: {ex.Message}");
            }

            // エンコーダで書き込み
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using var outStream = File.OpenWrite(outputPath);
            BitmapEncoder encoder;
            if (format.Equals("TIFF", StringComparison.OrdinalIgnoreCase))
            {
                encoder = new TiffBitmapEncoder();
            }
            else if (format.Equals("PNG", StringComparison.OrdinalIgnoreCase))
            {
                encoder = new PngBitmapEncoder();
            }
            else
            {
                encoder = new JpegBitmapEncoder { QualityLevel = jpegQuality };
            }

            encoder.Frames.Add(BitmapFrame.Create(frameSource, frameSource.Thumbnail, metadata, frameSource.ColorContexts));
            encoder.Save(outStream);
        }

        private bool TryParseShutterSpeed(string ss, out double seconds)
        {
            seconds = 0.004; // 1/250 default
            if (string.IsNullOrWhiteSpace(ss)) return false;

            ss = ss.Trim().Replace("s", "", StringComparison.OrdinalIgnoreCase).Replace("秒", "");
            if (ss.Contains('/'))
            {
                var parts = ss.Split('/');
                if (parts.Length == 2 &&
                    double.TryParse(parts[0], out double num) &&
                    double.TryParse(parts[1], out double den) &&
                    den > 0)
                {
                    seconds = num / den;
                    return true;
                }
            }
            else if (double.TryParse(ss, out double val))
            {
                seconds = val;
                return true;
            }

            return false;
        }
    }
}
