using System.IO;
using System.Text.Json;
using IrisPxS.Models;
using OpenCvSharp;

namespace IrisPxS.Services
{
    public class RollSessionService
    {
        private readonly string _baseSessionDir;

        public RollSessionService()
        {
            _baseSessionDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "IrisPxS",
                "Sessions"
            );

            if (!Directory.Exists(_baseSessionDir))
            {
                Directory.CreateDirectory(_baseSessionDir);
            }
        }

        public string GetSessionDirectory(string sessionId)
        {
            var dir = Path.Combine(_baseSessionDir, sessionId);
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            return dir;
        }

        /// <summary>
        /// ストリップ画像（全体生データおよびIRデータ）を一時領域に保存
        /// </summary>
        public (string rawPath, string? irPath) SaveStripImages(string sessionId, string stripId, Mat rawMat, Mat? irMat)
        {
            var sessionDir = GetSessionDirectory(sessionId);
            var stripDir = Path.Combine(sessionDir, "Strips");
            if (!Directory.Exists(stripDir))
            {
                Directory.CreateDirectory(stripDir);
            }

            string rawPath = Path.Combine(stripDir, $"{stripId}_raw.png");
            Cv2.ImWrite(rawPath, rawMat);

            string? irPath = null;
            if (irMat != null && !irMat.Empty())
            {
                irPath = Path.Combine(stripDir, $"{stripId}_ir.png");
                Cv2.ImWrite(irPath, irMat);
            }

            return (rawPath, irPath);
        }

        /// <summary>
        /// 個別コマの切り出し画像を一時領域にキャッシュ保存
        /// </summary>
        public string SaveFrameRawImage(string sessionId, string frameId, Mat frameMat)
        {
            var sessionDir = GetSessionDirectory(sessionId);
            var framesDir = Path.Combine(sessionDir, "Frames");
            if (!Directory.Exists(framesDir))
            {
                Directory.CreateDirectory(framesDir);
            }

            string framePath = Path.Combine(framesDir, $"{frameId}_raw.png");
            Cv2.ImWrite(framePath, frameMat);
            return framePath;
        }

        /// <summary>
        /// セッション情報の保存
        /// </summary>
        public void SaveSessionMetadata(RollSession session)
        {
            var sessionDir = GetSessionDirectory(session.SessionId);
            var metaPath = Path.Combine(sessionDir, "session.json");

            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(session, options);
            File.WriteAllText(metaPath, json);
        }
    }
}
