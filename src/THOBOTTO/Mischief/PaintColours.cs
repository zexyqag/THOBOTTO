using System.Globalization;

using NetCord;

namespace THOBOTTO.Mischief;

public static class PaintColours
{
    public static IReadOnlyDictionary<string, int> Named { get; } = new Dictionary<string, int>
    {
        ["red"] = 0xE74C3C,
        ["orange"] = 0xE67E22,
        ["yellow"] = 0xF1C40F,
        ["gold"] = 0xD4AF37,
        ["lime"] = 0x7CFC00,
        ["green"] = 0x2ECC71,
        ["teal"] = 0x1ABC9C,
        ["cyan"] = 0x00FFFF,
        ["blue"] = 0x3498DB,
        ["navy"] = 0x1F3A93,
        ["purple"] = 0x9B59B6,
        ["magenta"] = 0xFF00FF,
        ["pink"] = 0xFF69B4,
        ["brown"] = 0x8B4513,
        ["beige"] = 0xF5F5DC,
        ["grey"] = 0x95A5A6,
        ["white"] = 0xFFFFFF,
        // Discord treats colour 0 as "no colour", so black is the nearest visible shade.
        ["black"] = 0x010101,
    };

    // A colour name or a hex code such as #ff00ff.
    public static bool TryParse(string input, out Color colour, out string name)
    {
        input = input.Trim().ToLowerInvariant();
        if (Named.TryGetValue(input, out var rgb))
        {
            (colour, name) = (new(rgb), input);
            return true;
        }

        var hex = input.TrimStart('#');
        if (hex.Length == 6 && int.TryParse(hex, NumberStyles.HexNumber, null, out rgb))
        {
            (colour, name) = (new(Math.Max(rgb, 1)), $"#{hex}");
            return true;
        }

        (colour, name) = (default, "");
        return false;
    }
}
