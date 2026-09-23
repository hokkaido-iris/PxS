namespace IrisPxS.Models
{
    public class FilmProfile
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public bool IsMonochrome { get; set; }

        // チャネルごとのガンマ値 (NP変換時の階調カーブ)
        public double GammaR { get; set; } = 1.0;
        public double GammaG { get; set; } = 1.0;
        public double GammaB { get; set; } = 1.0;

        // ハイライト・シャドウのオフセット
        public double BlackPointOffset { get; set; } = 0.02;
        public double WhitePointOffset { get; set; } = 0.98;

        // 彩度倍率
        public double SaturationMultiplier { get; set; } = 1.0;

        // コントラスト調整係数
        public double Contrast { get; set; } = 1.0;

        // ベースカラー推定のデフォルトヒント (R, G, B: 0~255)
        public (byte R, byte G, byte B)? TypicalBaseColor { get; set; }

        public static List<FilmProfile> GetPresetProfiles()
        {
            return new List<FilmProfile>
            {
                new FilmProfile
                {
                    Id = "portra400",
                    Name = "Kodak Portra 400",
                    Description = "滑らかなスキントーン、高ダイナミックレンジ、自然な暖かみ",
                    GammaR = 1.02,
                    GammaG = 1.0,
                    GammaB = 0.96,
                    Contrast = 1.08,
                    SaturationMultiplier = 1.05,
                    TypicalBaseColor = (215, 125, 75)
                },
                new FilmProfile
                {
                    Id = "gold200",
                    Name = "Kodak Gold 200",
                    Description = "温かみのあるゴールデンな発色、鮮やかな赤・黄、どこか懐かしいトーン",
                    GammaR = 1.06,
                    GammaG = 1.02,
                    GammaB = 0.92,
                    Contrast = 1.15,
                    SaturationMultiplier = 1.20,
                    TypicalBaseColor = (220, 115, 65)
                },
                new FilmProfile
                {
                    Id = "ultramax400",
                    Name = "Kodak UltraMax 400",
                    Description = "高コントラスト＆高彩度、青と赤が印象的に際立つポップなトーン",
                    GammaR = 1.04,
                    GammaG = 1.0,
                    GammaB = 0.94,
                    Contrast = 1.22,
                    SaturationMultiplier = 1.25,
                    TypicalBaseColor = (225, 120, 70)
                },
                new FilmProfile
                {
                    Id = "pro400h",
                    Name = "Fujifilm Pro 400H",
                    Description = "透明感のあるパステル調、ミントグリーンと爽やかなシアンブルー、柔らかなハイライト",
                    GammaR = 0.97,
                    GammaG = 1.03,
                    GammaB = 1.04,
                    Contrast = 1.05,
                    SaturationMultiplier = 1.08,
                    TypicalBaseColor = (210, 130, 80)
                },
                new FilmProfile
                {
                    Id = "superia400",
                    Name = "Fujifilm Superia X-TRA 400",
                    Description = "深みのある豊かなエメラルドグリーンと引き締まったシャドウ、高い日常汎用性",
                    GammaR = 0.98,
                    GammaG = 1.04,
                    GammaB = 1.02,
                    Contrast = 1.18,
                    SaturationMultiplier = 1.15,
                    TypicalBaseColor = (205, 125, 75)
                },
                new FilmProfile
                {
                    Id = "cinestill800t",
                    Name = "CineStill 800T",
                    Description = "タングステン光用映画用フィルム特有のシネマティックなブルーと独特のハレーション感",
                    GammaR = 0.92,
                    GammaG = 0.98,
                    GammaB = 1.12,
                    Contrast = 1.20,
                    SaturationMultiplier = 1.15,
                    TypicalBaseColor = (230, 100, 60)
                },
                new FilmProfile
                {
                    Id = "bw_hp5",
                    Name = "B&W - Ilford HP5+ / Kodak Tri-X",
                    Description = "豊かなクラシック銀塩モノクロ階調、重厚なシャドウとシャープな粒状感",
                    IsMonochrome = true,
                    GammaR = 1.0,
                    GammaG = 1.0,
                    GammaB = 1.0,
                    Contrast = 1.25,
                    SaturationMultiplier = 0.0,
                    TypicalBaseColor = (180, 180, 180)
                },
                new FilmProfile
                {
                    Id = "neutral",
                    Name = "Neutral (ニュートラル反転)",
                    Description = "色付けを行わないリニアな数学的反転（後処理編集向け）",
                    GammaR = 1.0,
                    GammaG = 1.0,
                    GammaB = 1.0,
                    Contrast = 1.0,
                    SaturationMultiplier = 1.0
                }
            };
        }
    }
}
