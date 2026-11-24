using FifineCaptureStudio.Core.Monitoring;

namespace FifineCaptureStudio.Rendering;

/// <summary>
/// Composites the compact FPS readout into the native presenter's BGRA frame. It is intentionally
/// plain block text: no WPF overlay window can sit reliably above a 120–280 Hz flip-model swapchain,
/// and a separate compositor would add a second presentation path. The text layer is built once per
/// snapshot and blended into each frame, so the presenter thread never allocates or parses glyphs.
/// </summary>
public static class FpsOverlayCompositor
{
    private static readonly Dictionary<char, string[]> Glyphs = BuildGlyphs();
    private static readonly object CacheGate = new();
    private static FpsOverlaySnapshot? _cachedSnapshot;
    private static int _cachedFrameWidth;
    private static int _cachedFrameHeight;
    private static byte[]? _cachedLayer;
    private static int _layerWidth;
    private static int _layerHeight;
    private static int _layerLeft;
    private static int _layerTop;

    private static Dictionary<char, string[]> BuildGlyphs()
    {
        var compact = new Dictionary<char, string>
        {
            [' '] = "000/000/000/000/000",
            ['0'] = "111/101/101/101/111",
            ['1'] = "010/110/010/010/111",
            ['2'] = "111/001/111/100/111",
            ['3'] = "111/001/111/001/111",
            ['4'] = "101/101/111/001/001",
            ['5'] = "111/100/111/001/111",
            ['6'] = "111/100/111/101/111",
            ['7'] = "111/001/010/010/010",
            ['8'] = "111/101/111/101/111",
            ['9'] = "111/101/111/001/111",
            ['A'] = "010/101/111/101/101",
            ['B'] = "110/101/110/101/110",
            ['C'] = "011/100/100/100/011",
            ['D'] = "110/101/101/101/110",
            ['E'] = "111/100/110/100/111",
            ['F'] = "111/100/110/100/100",
            ['G'] = "011/100/101/101/011",
            ['H'] = "101/101/111/101/101",
            ['I'] = "111/010/010/010/111",
            ['J'] = "001/001/001/101/010",
            ['K'] = "101/101/110/101/101",
            ['L'] = "100/100/100/100/111",
            ['M'] = "101/111/111/101/101",
            ['N'] = "101/111/111/111/101",
            ['O'] = "010/101/101/101/010",
            ['P'] = "110/101/110/100/100",
            ['Q'] = "010/101/101/111/011",
            ['R'] = "110/101/110/101/101",
            ['S'] = "011/100/010/001/110",
            ['T'] = "111/010/010/010/010",
            ['U'] = "101/101/101/101/111",
            ['V'] = "101/101/101/101/010",
            ['W'] = "101/101/111/111/101",
            ['X'] = "101/101/010/101/101",
            ['Y'] = "101/101/010/010/010",
            ['Z'] = "111/001/010/100/111",
            ['.'] = "000/000/000/000/010",
            ['%'] = "101/001/010/100/101",
            ['~'] = "000/011/110/000/000",
            ['-'] = "000/000/111/000/000",
            [':'] = "000/010/000/010/000",
            ['/'] = "001/001/010/100/100"
        };
        return compact.ToDictionary(pair => pair.Key, pair => pair.Value.Split('/'));
    }

    public static void Draw(byte[] bgra, int width, int height, FpsOverlaySnapshot snapshot)
    {
        if (!snapshot.Visible || width <= 0 || height <= 0 || bgra.Length < width * height * 4) return;

        try
        {
            lock (CacheGate)
            {
                if (_cachedLayer is null || !Equals(_cachedSnapshot, snapshot) ||
                    _cachedFrameWidth != width || _cachedFrameHeight != height)
                {
                    BuildLayer(width, height, snapshot);
                }

                Blend(bgra, width, height);
            }
        }
        catch
        {
            // A malformed snapshot must never stop the presenter.
        }
    }

    private static void BuildLayer(int frameWidth, int frameHeight, FpsOverlaySnapshot snapshot)
    {
        var baseScale = Math.Max(2, Math.Min(4, frameHeight / 180));
        var scale = Math.Clamp((int)Math.Round(baseScale * snapshot.Scale), 1, 6);
        var charWidth = 6 * scale;
        var charHeight = 7 * scale;
        var padding = 5 * scale;
        var lineGap = 3 * scale;

        var lines = new[]
        {
            $"GAME {snapshot.Game}   FRAME GEN {snapshot.FrameGenTotal}",
            $"GEN {snapshot.Generated}   PRES {snapshot.Presented}   IN {snapshot.Input}",
            snapshot.Qualifier
        };
        var columns = lines.Max(line => line.Length);
        var boxWidth = Math.Min(frameWidth, (columns * charWidth) + (padding * 2));
        var boxHeight = Math.Min(frameHeight, (lines.Length * charHeight) + ((lines.Length - 1) * lineGap) + (padding * 2));
        var margin = 14;
        var left = snapshot.Corner is OverlayCorner.TopRight or OverlayCorner.BottomRight
            ? frameWidth - boxWidth - margin
            : margin;
        var top = snapshot.Corner is OverlayCorner.BottomLeft or OverlayCorner.BottomRight
            ? frameHeight - boxHeight - margin
            : margin;

        var layer = new byte[Math.Max(1, boxWidth * boxHeight * 4)];
        for (var i = 0; i < layer.Length; i += 4)
        {
            layer[i + 3] = 165;
        }

        var y = padding;
        foreach (var line in lines)
        {
            DrawText(layer, boxWidth, boxHeight, line, padding, y, scale);
            y += charHeight + lineGap;
        }

        _cachedSnapshot = snapshot;
        _cachedFrameWidth = frameWidth;
        _cachedFrameHeight = frameHeight;
        _cachedLayer = layer;
        _layerWidth = boxWidth;
        _layerHeight = boxHeight;
        _layerLeft = left;
        _layerTop = top;
    }

    private static void Blend(byte[] destination, int frameWidth, int frameHeight)
    {
        var layer = _cachedLayer;
        if (layer is null) return;
        var x0 = Math.Max(0, _layerLeft);
        var y0 = Math.Max(0, _layerTop);
        var x1 = Math.Min(frameWidth, _layerLeft + _layerWidth);
        var y1 = Math.Min(frameHeight, _layerTop + _layerHeight);
        for (var y = y0; y < y1; y++)
        {
            var sourceRow = (y - _layerTop) * _layerWidth * 4;
            var destinationRow = y * frameWidth * 4;
            for (var x = x0; x < x1; x++)
            {
                var s = sourceRow + ((x - _layerLeft) * 4);
                var d = destinationRow + (x * 4);
                var alpha = layer[s + 3];
                if (alpha == 0) continue;
                var inverse = 255 - alpha;
                destination[d] = (byte)((layer[s] * alpha + destination[d] * inverse) / 255);
                destination[d + 1] = (byte)((layer[s + 1] * alpha + destination[d + 1] * inverse) / 255);
                destination[d + 2] = (byte)((layer[s + 2] * alpha + destination[d + 2] * inverse) / 255);
                destination[d + 3] = 255;
            }
        }
    }

    private static void DrawText(byte[] layer, int width, int height, string text, int x, int y, int scale)
    {
        foreach (var raw in text.ToUpperInvariant())
        {
            var c = raw switch
            {
                '·' => '.',
                '—' => '-',
                '≈' => '~',
                >= '0' and <= '9' => raw,
                >= 'A' and <= 'Z' => raw,
                _ => Glyphs.ContainsKey(raw) ? raw : ' '
            };

            if (!Glyphs.TryGetValue(c, out var rows))
            {
                x += 6 * scale;
                continue;
            }

            for (var gy = 0; gy < 5; gy++)
            {
                for (var gx = 0; gx < 3; gx++)
                {
                    if (rows[gy][gx] != '1') continue;
                    Fill(layer, width, height, x + gx * scale, y + gy * scale, scale, scale);
                }
            }

            x += 6 * scale;
        }
    }

    private static void Fill(byte[] layer, int width, int height, int x, int y, int w, int h)
    {
        var x0 = Math.Max(0, x);
        var y0 = Math.Max(0, y);
        var x1 = Math.Min(width, x + w);
        var y1 = Math.Min(height, y + h);
        for (var py = y0; py < y1; py++)
        {
            for (var px = x0; px < x1; px++)
            {
                var offset = ((py * width) + px) * 4;
                layer[offset] = 245;
                layer[offset + 1] = 246;
                layer[offset + 2] = 250;
                layer[offset + 3] = 255;
            }
        }
    }
}
