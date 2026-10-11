namespace GeckoPeeks.Ui;

internal static class Theme
{
    public static readonly Color Jungle = Color.FromArgb(16, 40, 30);
    public static readonly Color JungleDeep = Color.FromArgb(10, 28, 20);
    public static readonly Color Charcoal = Color.FromArgb(28, 34, 30);
    public static readonly Color Panel = Color.FromArgb(38, 48, 42);
    public static readonly Color Cream = Color.FromArgb(246, 242, 232);
    /// <summary>Secondary text — kept bright enough on charcoal/jungle.</summary>
    public static readonly Color CreamMuted = Color.FromArgb(214, 208, 192);
    /// <summary>Header/accent — light gold, not dark orange on near-black.</summary>
    public static readonly Color Amber = Color.FromArgb(240, 200, 110);
    public static readonly Color Coral = Color.FromArgb(230, 130, 96);
    public static readonly Color Leaf = Color.FromArgb(140, 196, 132);
    public static readonly Color Line = Color.FromArgb(58, 74, 64);

    public static readonly Font Title = new("Segoe UI", 18f, FontStyle.Bold, GraphicsUnit.Point);
    public static readonly Font Body = new("Segoe UI", 11f, FontStyle.Regular, GraphicsUnit.Point);
    public static readonly Font Small = new("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point);
    public static readonly Font Section = new("Segoe UI", 12f, FontStyle.Bold, GraphicsUnit.Point);
    /// <summary>Hop RTT / summary averages — slightly larger for scanability.</summary>
    public static readonly Font GridNumber = new("Segoe UI", 11f, FontStyle.Bold, GraphicsUnit.Point);
}
