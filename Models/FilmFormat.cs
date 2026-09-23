namespace IrisPxS.Models
{
    public enum FilmSizeCategory
    {
        Size135,
        Size120,
        Size127,
        Size110
    }

    public enum FilmFormatType
    {
        // 135
        Format135_Full,       // Full-Frame (24x36mm)
        Format135_Half,       // Half-Frame (18x24mm)

        // 120
        Format120_645,        // 645 (56x41.5mm)
        Format120_66,         // 66 (56x56mm)
        Format120_67,         // 67 (56x70mm)
        Format120_69,         // 69 (56x84mm)

        // 127
        Format127_465,        // 465 (40x65mm)
        Format127_44,         // 44 (40x40mm)
        Format127_43,         // 43 (40x30mm)

        // 110
        Format110_General,    // General (13x17mm)

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
                // 135
                new FilmFormat
                {
                    Type = FilmFormatType.Format135_Full,
                    Category = FilmSizeCategory.Size135,
                    DisplayName = "Full-Frame (24×36mm)",
                    ShortName = "Full-Frame",
                    PhysicalWidthMm = 36.0,
                    PhysicalHeightMm = 24.0,
                    DefaultFramesPerStrip = 6
                },
                new FilmFormat
                {
                    Type = FilmFormatType.Format135_Half,
                    Category = FilmSizeCategory.Size135,
                    DisplayName = "Half-Frame (18×24mm)",
                    ShortName = "Half-Frame",
                    PhysicalWidthMm = 18.0,
                    PhysicalHeightMm = 24.0,
                    DefaultFramesPerStrip = 12
                },

                // 120
                new FilmFormat
                {
                    Type = FilmFormatType.Format120_645,
                    Category = FilmSizeCategory.Size120,
                    DisplayName = "645 (56×42mm)",
                    ShortName = "645",
                    PhysicalWidthMm = 56.0,
                    PhysicalHeightMm = 41.5,
                    DefaultFramesPerStrip = 4
                },
                new FilmFormat
                {
                    Type = FilmFormatType.Format120_66,
                    Category = FilmSizeCategory.Size120,
                    DisplayName = "66 (56×56mm)",
                    ShortName = "66",
                    PhysicalWidthMm = 56.0,
                    PhysicalHeightMm = 56.0,
                    DefaultFramesPerStrip = 3
                },
                new FilmFormat
                {
                    Type = FilmFormatType.Format120_67,
                    Category = FilmSizeCategory.Size120,
                    DisplayName = "67 (56×70mm)",
                    ShortName = "67",
                    PhysicalWidthMm = 70.0,
                    PhysicalHeightMm = 56.0,
                    DefaultFramesPerStrip = 2
                },
                new FilmFormat
                {
                    Type = FilmFormatType.Format120_69,
                    Category = FilmSizeCategory.Size120,
                    DisplayName = "69 (56×84mm)",
                    ShortName = "69",
                    PhysicalWidthMm = 84.0,
                    PhysicalHeightMm = 56.0,
                    DefaultFramesPerStrip = 2
                },

                // 127
                new FilmFormat
                {
                    Type = FilmFormatType.Format127_465,
                    Category = FilmSizeCategory.Size127,
                    DisplayName = "465 (40×65mm)",
                    ShortName = "465",
                    PhysicalWidthMm = 65.0,
                    PhysicalHeightMm = 40.0,
                    DefaultFramesPerStrip = 3
                },
                new FilmFormat
                {
                    Type = FilmFormatType.Format127_44,
                    Category = FilmSizeCategory.Size127,
                    DisplayName = "44 (40×40mm)",
                    ShortName = "44",
                    PhysicalWidthMm = 40.0,
                    PhysicalHeightMm = 40.0,
                    DefaultFramesPerStrip = 3
                },
                new FilmFormat
                {
                    Type = FilmFormatType.Format127_43,
                    Category = FilmSizeCategory.Size127,
                    DisplayName = "43 (40×30mm)",
                    ShortName = "43",
                    PhysicalWidthMm = 40.0,
                    PhysicalHeightMm = 30.0,
                    DefaultFramesPerStrip = 4
                },

                // 110
                new FilmFormat
                {
                    Type = FilmFormatType.Format110_General,
                    Category = FilmSizeCategory.Size110,
                    DisplayName = "General (13×17mm)",
                    ShortName = "General",
                    PhysicalWidthMm = 17.0,
                    PhysicalHeightMm = 13.0,
                    DefaultFramesPerStrip = 6
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
