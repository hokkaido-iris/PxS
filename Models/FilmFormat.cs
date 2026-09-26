namespace IrisPxS.Models
{
    public enum FilmSizeCategory
    {
        Size135,
        Size120,
        Size127,
        Size240,
        Size110
    }

    public enum FilmFormatType
    {
        // 135
        Format135_Full,       // 135 FF (Full Frame)
        Format135_Half,       // 135 HF (Half Frame)
        Format135_24x65,      // 135 24x65 (Panorama)

        // 120
        Format120_645,        // 120 6x4.5
        Format120_66,         // 120 6x6
        Format120_67,         // 120 6x7
        Format120_68,         // 120 6x8
        Format120_69,         // 120 6x9

        // 127
        Format127_43,         // 127 4x3
        Format127_44,         // 127 4x4
        Format127_465,        // 127 4x6.5

        // 240
        Format240_HiVision,   // 240 HiVision (9:16)
        Format240_Classic,    // 240 Classic (2:3)
        Format240_Panorama,   // 240 Panorama (1:3)

        // 110
        Format110_General,    // 110 13x17

        // Custom
        Custom
    }

    public class FilmFormat
    {
        public FilmFormatType Type { get; set; }
        public FilmSizeCategory Category { get; set; }
        public string DisplayName { get; set; } = string.Empty;
        public string ShortName { get; set; } = string.Empty;
        public double PhysicalWidthMm { get; set; }
        public double PhysicalHeightMm { get; set; }
        public double AspectRatio => PhysicalWidthMm / (PhysicalHeightMm > 0 ? PhysicalHeightMm : 1.0);
        public int DefaultFramesPerStrip { get; set; }

        public static List<FilmFormat> GetAllFormats()
        {
            return new List<FilmFormat>
            {
                // 135 (35mm)
                new FilmFormat
                {
                    Type = FilmFormatType.Format135_Full,
                    Category = FilmSizeCategory.Size135,
                    DisplayName = "135 FF (Full Frame)",
                    ShortName = "135 FF",
                    PhysicalWidthMm = 36.0,
                    PhysicalHeightMm = 24.0,
                    DefaultFramesPerStrip = 6
                },
                new FilmFormat
                {
                    Type = FilmFormatType.Format135_Half,
                    Category = FilmSizeCategory.Size135,
                    DisplayName = "135 HF (Half Frame)",
                    ShortName = "135 HF",
                    PhysicalWidthMm = 18.0,
                    PhysicalHeightMm = 24.0,
                    DefaultFramesPerStrip = 12
                },
                new FilmFormat
                {
                    Type = FilmFormatType.Format135_24x65,
                    Category = FilmSizeCategory.Size135,
                    DisplayName = "135 24x65 (Panorama)",
                    ShortName = "135 24x65",
                    PhysicalWidthMm = 65.0,
                    PhysicalHeightMm = 24.0,
                    DefaultFramesPerStrip = 3
                },

                // 120 (中判)
                new FilmFormat
                {
                    Type = FilmFormatType.Format120_645,
                    Category = FilmSizeCategory.Size120,
                    DisplayName = "120 6x4.5",
                    ShortName = "6x4.5",
                    PhysicalWidthMm = 56.0,
                    PhysicalHeightMm = 41.5,
                    DefaultFramesPerStrip = 4
                },
                new FilmFormat
                {
                    Type = FilmFormatType.Format120_66,
                    Category = FilmSizeCategory.Size120,
                    DisplayName = "120 6x6",
                    ShortName = "6x6",
                    PhysicalWidthMm = 56.0,
                    PhysicalHeightMm = 56.0,
                    DefaultFramesPerStrip = 3
                },
                new FilmFormat
                {
                    Type = FilmFormatType.Format120_67,
                    Category = FilmSizeCategory.Size120,
                    DisplayName = "120 6x7",
                    ShortName = "6x7",
                    PhysicalWidthMm = 70.0,
                    PhysicalHeightMm = 56.0,
                    DefaultFramesPerStrip = 2
                },
                new FilmFormat
                {
                    Type = FilmFormatType.Format120_68,
                    Category = FilmSizeCategory.Size120,
                    DisplayName = "120 6x8",
                    ShortName = "6x8",
                    PhysicalWidthMm = 76.0,
                    PhysicalHeightMm = 56.0,
                    DefaultFramesPerStrip = 2
                },
                new FilmFormat
                {
                    Type = FilmFormatType.Format120_69,
                    Category = FilmSizeCategory.Size120,
                    DisplayName = "120 6x9",
                    ShortName = "6x9",
                    PhysicalWidthMm = 84.0,
                    PhysicalHeightMm = 56.0,
                    DefaultFramesPerStrip = 2
                },

                // 127 (ベスト判)
                new FilmFormat
                {
                    Type = FilmFormatType.Format127_43,
                    Category = FilmSizeCategory.Size127,
                    DisplayName = "127 4x3",
                    ShortName = "4x3",
                    PhysicalWidthMm = 40.0,
                    PhysicalHeightMm = 30.0,
                    DefaultFramesPerStrip = 4
                },
                new FilmFormat
                {
                    Type = FilmFormatType.Format127_44,
                    Category = FilmSizeCategory.Size127,
                    DisplayName = "127 4x4",
                    ShortName = "4x4",
                    PhysicalWidthMm = 40.0,
                    PhysicalHeightMm = 40.0,
                    DefaultFramesPerStrip = 3
                },
                new FilmFormat
                {
                    Type = FilmFormatType.Format127_465,
                    Category = FilmSizeCategory.Size127,
                    DisplayName = "127 4x6.5",
                    ShortName = "4x6.5",
                    PhysicalWidthMm = 65.0,
                    PhysicalHeightMm = 40.0,
                    DefaultFramesPerStrip = 3
                },

                // 240 (APS)
                new FilmFormat
                {
                    Type = FilmFormatType.Format240_HiVision,
                    Category = FilmSizeCategory.Size240,
                    DisplayName = "240 HiVision (9:16)",
                    ShortName = "240 H",
                    PhysicalWidthMm = 30.2,
                    PhysicalHeightMm = 16.7,
                    DefaultFramesPerStrip = 6
                },
                new FilmFormat
                {
                    Type = FilmFormatType.Format240_Classic,
                    Category = FilmSizeCategory.Size240,
                    DisplayName = "240 Classic (2:3)",
                    ShortName = "240 C",
                    PhysicalWidthMm = 25.1,
                    PhysicalHeightMm = 16.7,
                    DefaultFramesPerStrip = 6
                },
                new FilmFormat
                {
                    Type = FilmFormatType.Format240_Panorama,
                    Category = FilmSizeCategory.Size240,
                    DisplayName = "240 Panorama (1:3)",
                    ShortName = "240 P",
                    PhysicalWidthMm = 30.2,
                    PhysicalHeightMm = 10.1,
                    DefaultFramesPerStrip = 6
                },

                // 110 (ポケット)
                new FilmFormat
                {
                    Type = FilmFormatType.Format110_General,
                    Category = FilmSizeCategory.Size110,
                    DisplayName = "110 13x17",
                    ShortName = "110 13x17",
                    PhysicalWidthMm = 17.0,
                    PhysicalHeightMm = 13.0,
                    DefaultFramesPerStrip = 8
                }
            };
        }

        public static List<FilmFormat> GetPresetFormats() => GetAllFormats();

        public static List<FilmFormat> GetFormatsByCategory(FilmSizeCategory category)
        {
            return GetAllFormats().FindAll(f => f.Category == category);
        }
    }
}
