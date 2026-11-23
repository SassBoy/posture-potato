namespace PosturePotato;

public enum EyeState { Open, Closed, Happy }
public enum MouthState { Meh, Smile }
public enum ArmState { Down, WaveA, WaveB, Cheer }

/// <summary>Everything that can change between frames.</summary>
public struct PotatoPose
{
    public int Lift;      // art pixels, + is down (into the soil)
    public int Lean;      // shear at the top of the sprite, art pixels (- leans left)
    public int Squash;    // + shorter, - taller
    public int Sprout;    // -1 / 0 / 1 leaf sway
    public EyeState Eyes;
    public MouthState Mouth;
    public ArmState Arms;
    public int Sparkle;   // 0 off, 1 or 2 twinkle frame
}

/// <summary>
/// Pixel-art Posture Potato. The body is a 32x32 sprite; it sits on a 40x46 canvas that leaves
/// headroom for hops and sparkles. Rows 44+ are below the soil line.
/// </summary>
public static class PotatoArt
{
    public const int W = 40, H = 46, SoilRow = 44;
    private const int SW = 40, SH = 32, OX = 4, RestY = 12;

    private const uint T = 0x00000000;
    private const uint Outline = 0xFF4A2F1A, Shade = 0xFFB0783D, Base = 0xFFD6A35A, Light = 0xFFEAC07B, Hi = 0xFFF6DCA3;
    private const uint Spot = 0xFF9A6834, Eye = 0xFF241811, EyeHi = 0xFFFFFFFF, Cheek = 0xFFE0897A, Mouth = 0xFF7A3226;
    private const uint Leaf = 0xFF8DBE6C, LeafDark = 0xFF4D7838, Shadow = 0x38000000;
    private const uint Spark = 0xFFF2D27A, SparkCore = 0xFFFFFFFF;

    private static readonly uint[] BodySprite = BuildBody();
    private static readonly (int left, int right) ArmRow = FindEdges(21);

    // ---------------- body ----------------

    private static bool Inside(int x, int y)
    {
        double dx = x + 0.5 - 16.5, dy = y + 0.5 - 19.5;
        const double ry = 11.2;
        double rx = 10.2 + 0.9 * (dy / ry);                     // a touch wider at the bottom
        double a = Math.Atan2(dy, dx);
        double lump = 1 + 0.035 * Math.Sin(3 * a + 0.8) + 0.02 * Math.Sin(5 * a);
        double nx = dx / rx, ny = dy / ry;
        return nx * nx + ny * ny <= lump * lump;
    }

    private static uint[] BuildBody()
    {
        var s = new uint[SW * SH];
        for (int y = 0; y < SH; y++)
        for (int x = 0; x < 32; x++)
        {
            if (!Inside(x, y)) continue;
            bool edge = !Inside(x - 1, y) || !Inside(x + 1, y) || !Inside(x, y - 1) || !Inside(x, y + 1);
            uint c;
            if (edge) c = Outline;
            else
            {
                double dx = x + 0.5 - 16.5, dy = y + 0.5 - 19.5;
                double nx = dx / 10.2, ny = dy / 11.2;
                double d = Math.Sqrt((nx + 0.38) * (nx + 0.38) + (ny + 0.46) * (ny + 0.46));
                c = d < 0.26 ? Hi : d < 0.68 ? Light : d < 1.12 ? Base : Shade;
            }
            s[y * SW + x + OX] = c;
        }
        // Potato "eyes" (skin spots): only on plain skin.
        foreach (var (x, y) in new[] { (21, 11), (22, 11), (9, 25), (10, 25), (23, 26), (11, 13), (25, 18) })
        {
            ref uint p = ref s[y * SW + x + OX];
            if (p != T && p != Outline) p = Spot;
        }
        return s;
    }

    private static (int, int) FindEdges(int row)
    {
        int l = 31, r = 0;
        for (int x = 0; x < 32; x++)
            if (BodySprite[row * SW + x + OX] != T) { l = Math.Min(l, x); r = Math.Max(r, x); }
        return (l, r);
    }

    // ---------------- composing ----------------

    private static void P(uint[] s, int x, int y, uint c)
    {
        x += OX;
        if ((uint)x < SW && (uint)y < SH) s[y * SW + x] = c;
    }

    /// <summary>The 40x32 sprite (body coords offset by 4) for a pose, before squash/shear.</summary>
    private static uint[] Sprite(in PotatoPose p)
    {
        var s = (uint[])BodySprite.Clone();

        // Eyes: 2x3 slots at (12,17) and (19,17)
        foreach (int ex in new[] { 12, 19 })
        {
            const int ey = 17;
            switch (p.Eyes)
            {
                case EyeState.Open:
                    P(s, ex, ey, EyeHi); P(s, ex + 1, ey, Eye);
                    P(s, ex, ey + 1, Eye); P(s, ex + 1, ey + 1, Eye);
                    P(s, ex, ey + 2, Eye); P(s, ex + 1, ey + 2, Eye);
                    break;
                case EyeState.Closed:
                    P(s, ex, ey + 2, Eye); P(s, ex + 1, ey + 2, Eye);
                    break;
                case EyeState.Happy: // ^
                    P(s, ex - 1, ey + 2, Eye); P(s, ex, ey + 1, Eye); P(s, ex + 1, ey + 1, Eye); P(s, ex + 2, ey + 2, Eye);
                    break;
            }
        }

        // Cheeks
        P(s, 10, 20, Cheek); P(s, 11, 20, Cheek); P(s, 22, 20, Cheek); P(s, 23, 20, Cheek);

        // Mouth
        if (p.Mouth == MouthState.Smile)
        {
            P(s, 15, 22, Eye); P(s, 16, 22, Mouth); P(s, 17, 22, Mouth); P(s, 18, 22, Eye);
            P(s, 16, 23, Eye); P(s, 17, 23, Eye);
        }
        else
        {
            P(s, 16, 22, Eye); P(s, 17, 22, Eye);
        }

        // Sprout: leaves sway, stem stays planted
        string[] sprout =
        {
            ".....ggg.",
            ".gg.ggggG",
            "gggG.GGG.",
            ".GGGG....",
            "....G....",
            "....G....",
        };
        for (int r = 0; r < sprout.Length; r++)
        {
            int shift = r <= 2 ? p.Sprout : 0;
            for (int c = 0; c < sprout[r].Length; c++)
            {
                char ch = sprout[r][c];
                if (ch == '.') continue;
                P(s, 12 + c + shift, 2 + r, ch == 'g' ? Leaf : LeafDark);
            }
        }

        // Arms (1 px strokes off the row-21 edges)
        int L = ArmRow.left, R = ArmRow.right;
        void Arm(params (int x, int y)[] pts) { foreach (var (x, y) in pts) P(s, x, y, Outline); }
        switch (p.Arms)
        {
            case ArmState.Down:
                Arm((L - 1, 21), (L - 2, 22), (L - 2, 23));
                Arm((R + 1, 21), (R + 2, 22), (R + 2, 23));
                break;
            case ArmState.WaveA:
                Arm((L - 1, 21), (L - 2, 22), (L - 2, 23));
                Arm((R + 1, 20), (R + 2, 19), (R + 3, 18));
                break;
            case ArmState.WaveB:
                Arm((L - 1, 21), (L - 2, 22), (L - 2, 23));
                Arm((R + 1, 20), (R + 1, 19), (R + 2, 18));
                break;
            case ArmState.Cheer:
                Arm((L - 1, 20), (L - 2, 19), (L - 3, 18));
                Arm((R + 1, 20), (R + 2, 19), (R + 3, 18));
                break;
        }

        // Feet
        foreach (int fx in new[] { 12, 13, 14, 19, 20, 21 }) P(s, fx, 31, Outline);
        return s;
    }

    /// <summary>Full 40x46 frame (ARGB, row-major) for the pose.</summary>
    public static uint[] Render(in PotatoPose p)
    {
        var canvas = new uint[W * H];
        var s = Sprite(p);

        // Shadow on the soil, shrinking while the potato is in the air
        if (p.Lift <= 3)
        {
            int w = 16 - 2 * Math.Min(Math.Max(-p.Lift, 0) / 2, 3);
            for (int x = 21 - w / 2; x <= 20 + w / 2; x++) Put(canvas, x, 43, Shadow);
            for (int x = 23 - w / 2; x <= 18 + w / 2; x++) Put(canvas, x, 44, Shadow);
        }

        // Potato with squash (rows above the waist pivot move) and lean (shear, feet planted)
        const int pivot = 20;
        int y0 = RestY + p.Lift;
        for (int y = 0; y < SH; y++)
        {
            int src = y;
            if (y <= pivot)
            {
                src = y - p.Squash;
                if (p.Squash < 0 && src > pivot) src = pivot;
            }
            if ((uint)src >= SH) continue;
            int off = (int)Math.Round(p.Lean * (SH - 1 - y) / (double)(SH - 1), MidpointRounding.AwayFromZero);
            for (int x = 0; x < SW; x++)
            {
                uint c = s[src * SW + x];
                if (c != T) Put(canvas, x + off, y0 + y, c);
            }
        }

        if (p.Sparkle > 0)
        {
            foreach (var (cx, cy, big) in new[] { (5, 15, p.Sparkle == 1), (35, 10, p.Sparkle == 2), (36, 24, p.Sparkle == 1) })
            {
                Put(canvas, cx, cy, big ? SparkCore : Spark);
                int arm = big ? 2 : 1;
                for (int i = 1; i <= arm; i++)
                {
                    Put(canvas, cx - i, cy, Spark); Put(canvas, cx + i, cy, Spark);
                    Put(canvas, cx, cy - i, Spark); Put(canvas, cx, cy + i, Spark);
                }
            }
        }

        return canvas;
    }

    /// <summary>Just the 32x32 potato for icons.</summary>
    public static uint[] RenderIconSprite(in PotatoPose p)
    {
        var s = Sprite(p);
        var icon = new uint[32 * 32];
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
                icon[y * 32 + x] = s[y * SW + x + OX];
        return icon;
    }

    private static void Put(uint[] c, int x, int y, uint v)
    {
        if ((uint)x < W && (uint)y < H) c[y * W + x] = v;
    }
}
