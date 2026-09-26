using System.IO;
using System.Text.Json;

namespace IrisPxS.Services
{
    /// <summary>
    /// 各DPI・ビット深度ごとのスキャン実測時間を記録・学習し、スキャン残り時間の高精度カウントダウンを提供するトラッカー
    /// </summary>
    public class ScanDurationTracker
    {
        private static readonly string SettingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IrisPxS");

        private static readonly string DurationsFilePath = Path.Combine(SettingsDir, "scan_durations.json");

        // キー: "dpi_bitdepth" (例: "300_8", "2400_8", "2400_16")
        // 値: 平均スキャン所要時間（秒）
        private Dictionary<string, double> _durations = new();

        // 初回未計測時のデフォルト所要時間テーブル (秒)
        // GT-X820 等の標準的な透過原稿ユニット(TPU)スキャン時間目安
        private static readonly Dictionary<int, double> DefaultDurations8Bit = new()
        {
            { 300, 14.0 },   // PreScan / 低解像度
            { 600, 24.0 },
            { 1200, 48.0 },
            { 2400, 92.0 },  // 2400 DPI 標準
            { 3200, 135.0 }, // 3200 DPI 高精細
            { 4800, 195.0 },
            { 6400, 275.0 }, // 6400 DPI 光学最高
            { 9600, 380.0 }, // 補間
            { 12800, 510.0 } // 最大補間
        };

        public ScanDurationTracker()
        {
            LoadDurations();
        }

        private static string GetKey(int dpi, int bitDepth)
        {
            int bpc = (bitDepth == 16 || bitDepth == 48) ? 16 : 8;
            return $"{dpi}_{bpc}";
        }

        /// <summary>
        /// 指定されたDPIおよびビット深度における推定スキャン所要時間（秒）を取得
        /// </summary>
        public double GetEstimatedDurationSeconds(int dpi, int bitDepth = 8)
        {
            string key = GetKey(dpi, bitDepth);
            if (_durations.TryGetValue(key, out double recordedSec) && recordedSec > 2.0)
            {
                return recordedSec;
            }

            // 16bitで未記録の場合、8bit値の約 1.3〜1.5 倍を推定
            bool is16Bit = (bitDepth == 16 || bitDepth == 48);
            string fallback8Key = GetKey(dpi, 8);
            if (_durations.TryGetValue(fallback8Key, out double recorded8Sec) && recorded8Sec > 2.0)
            {
                return is16Bit ? recorded8Sec * 1.35 : recorded8Sec;
            }

            // デフォルトテーブルからの補間・取得
            if (DefaultDurations8Bit.TryGetValue(dpi, out double defaultSec))
            {
                return is16Bit ? defaultSec * 1.35 : defaultSec;
            }

            // テーブルにないDPIの場合、近傍値から比例計算
            var sortedKeys = DefaultDurations8Bit.Keys.OrderBy(k => k).ToList();
            if (dpi < sortedKeys.First())
            {
                double baseSec = DefaultDurations8Bit[sortedKeys.First()];
                return is16Bit ? baseSec * 1.35 : baseSec;
            }
            if (dpi > sortedKeys.Last())
            {
                double baseSec = DefaultDurations8Bit[sortedKeys.Last()];
                double extrapolated = baseSec * ((double)dpi / sortedKeys.Last());
                return is16Bit ? extrapolated * 1.35 : extrapolated;
            }

            for (int i = 0; i < sortedKeys.Count - 1; i++)
            {
                if (dpi >= sortedKeys[i] && dpi <= sortedKeys[i + 1])
                {
                    double t = (double)(dpi - sortedKeys[i]) / (sortedKeys[i + 1] - sortedKeys[i]);
                    double lerp = DefaultDurations8Bit[sortedKeys[i]] + t * (DefaultDurations8Bit[sortedKeys[i + 1]] - DefaultDurations8Bit[sortedKeys[i]]);
                    return is16Bit ? lerp * 1.35 : lerp;
                }
            }

            return is16Bit ? 120.0 : 90.0;
        }

        /// <summary>
        /// スキャン完了時の実測所要時間を記録し、学習して永続化保存
        /// </summary>
        public void RecordActualDuration(int dpi, int bitDepth, double actualSeconds)
        {
            if (actualSeconds < 1.0) return;

            string key = GetKey(dpi, bitDepth);
            if (_durations.TryGetValue(key, out double currentAvg) && currentAvg > 0)
            {
                // 指数移動平均 (最新の測定結果を 60%、過去の蓄積を 40% 反映)
                _durations[key] = Math.Round(currentAvg * 0.40 + actualSeconds * 0.60, 1);
            }
            else
            {
                _durations[key] = Math.Round(actualSeconds, 1);
            }

            SaveDurations();
        }

        private void LoadDurations()
        {
            try
            {
                if (File.Exists(DurationsFilePath))
                {
                    string json = File.ReadAllText(DurationsFilePath);
                    var dict = JsonSerializer.Deserialize<Dictionary<string, double>>(json);
                    if (dict != null)
                    {
                        _durations = dict;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load scan durations: {ex.Message}");
            }
        }

        private void SaveDurations()
        {
            try
            {
                if (!Directory.Exists(SettingsDir))
                {
                    Directory.CreateDirectory(SettingsDir);
                }

                string json = JsonSerializer.Serialize(_durations, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(DurationsFilePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save scan durations: {ex.Message}");
            }
        }
    }
}
