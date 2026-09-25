using System.Windows.Media;

namespace QuickAccess;

public static class ThemeColors
{
    public static bool TryParse(string? hex, out Color color)
    {
        color = Colors.White;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        try
        {
            string h = hex.Trim().TrimStart('#');
            if (h.Length == 6)
            {
                color = Color.FromArgb(0xFF,
                    Convert.ToByte(h.Substring(0, 2), 16),
                    Convert.ToByte(h.Substring(2, 2), 16),
                    Convert.ToByte(h.Substring(4, 2), 16));
                return true;
            }
            if (h.Length == 8)
            {
                color = Color.FromArgb(
                    Convert.ToByte(h.Substring(0, 2), 16),
                    Convert.ToByte(h.Substring(2, 2), 16),
                    Convert.ToByte(h.Substring(4, 2), 16),
                    Convert.ToByte(h.Substring(6, 2), 16));
                return true;
            }
        }
        catch { }
        return false;
    }

    public static Color Parse(string? hex, Color fallback) =>
        TryParse(hex, out var c) ? c : fallback;

    public static Color WithAlpha(Color c, byte a) =>
        Color.FromArgb(a, c.R, c.G, c.B);
}

public record ThemePreset(string Name, string Line, string Accent, string Background);

public static class ThemePresets
{
    public static readonly List<ThemePreset> All = new()
    {
        new ThemePreset("Monochrome", "#FFFFFF", "#FFFFFF", "#DC121215"),
        new ThemePreset("Cyberpunk", "#22D3EE", "#A855F7", "#DC0B0B18"),
        new ThemePreset("Dark Gold", "#E8C874", "#D4AF37", "#DC141210"),
    };
}
