namespace IrisPxS.Models
{
    public enum FilmFormatType
    {
        Format135_Full,       // 24x36mm (アスペクト比 1:1.5)
        Format135_Half,       // 18x24mm (アスペクト比 1:1.33)
        Format135_Panorama,   // 24x65mm (アスペクト比 1:2.7)
        Format120_6x45,       // 56x41.5mm (アスペクト比 1:1.35)
        Format120_6x6,        // 56x56mm (アスペクト比 1:1.0)
        Format120_6x7,        // 56x70mm (アスペクト比 1:1.25)
        Format120_6x9,        // 56x84mm (アスペクト比 1:1.5)
        Custom                // 自由矩形
    }

    public class FilmFormat
    {
        public FilmFormatType Type { get; set; }
        public string DisplayName { get; set; } = string.Empty;
        public double PhysicalWidthMm { get; set; }
        public double PhysicalHeightMm { get; set; }
        public double AspectRatio => PhysicalWidthMm / (PhysicalHeightMm > 0 ? PhysicalHeightMm : 1.0);
        public int DefaultFramesPerStrip { get; set; }

        public static List<FilmFormat> GetPresetFormats()
        {
            return new List<FilmFormat>
            {
                new FilmFormat
                {
                    Type = FilmFormatType.Format135_Full,
                    DisplayName = "35mm (135) フルサイズ (24×36mm)",
                    PhysicalWidthMm = 36.0,
                    PhysicalHeightMm = 24.0,
                    DefaultFramesPerStrip = 6
                },
                new FilmFormat
                {
                    Type = FilmFormatType.Format135_Half,
                    DisplayName = "35mm (135) ハーフサイズ (18×24mm)",
                    PhysicalWidthMm = 18.0,
                    PhysicalHeightMm = 24.0,
                    DefaultFramesPerStrip = 12
                },
                new FilmFormat
                {
                    Type = FilmFormatType.Format135_Panorama,
                    DisplayName = "35mm (135) パノラマ/XPan (24×65mm)",
                    PhysicalWidthMm = 65.0,
                    PhysicalHeightMm = 24.0,
                    DefaultFramesPerStrip = 3
                },
                new FilmFormat
                {
                    Type = FilmFormatType.Format120_6x45,
                    DisplayName = "中判 120 (6×4.5cm / 56×42mm)",
                    PhysicalWidthMm = 56.0,
                    PhysicalHeightMm = 41.5,
                    DefaultFramesPerStrip = 4
                },
                new FilmFormat
                {
                    Type = FilmFormatType.Format120_6x6,
                    DisplayName = "中判 120 (6×6cm 正方形 / 56×56mm)",
                    PhysicalWidthMm = 56.0,
                    PhysicalHeightMm = 56.0,
                    DefaultFramesPerStrip = 3
                },
                new FilmFormat
                {
                    Type = FilmFormatType.Format120_6x7,
                    DisplayName = "中判 120 (6×7cm / 56×70mm)",
                    PhysicalWidthMm = 70.0,
                    PhysicalHeightMm = 56.0,
                    DefaultFramesPerStrip = 2
                },
                new FilmFormat
                {
                    Type = FilmFormatType.Format120_6x9,
                    DisplayName = "中判 120 (6×9cm / 56×84mm)",
                    PhysicalWidthMm = 84.0,
                    PhysicalHeightMm = 56.0,
                    DefaultFramesPerStrip = 2
                },
                new FilmFormat
                {
                    Type = FilmFormatType.Custom,
                    DisplayName = "カスタム / フリーサイズ",
                    PhysicalWidthMm = 36.0,
                    PhysicalHeightMm = 24.0,
                    DefaultFramesPerStrip = 6
                }
            };
        }
    }
}
