namespace VitaSharp.Core.Shell;

/// <summary>An axis-aligned rectangle in LiveArea screen coordinates (960x544, origin top-left).</summary>
public readonly record struct LaRect(double X, double Y, double W, double H)
{
    public double Right => X + W;
    public double Bottom => Y + H;
    public bool Contains(double px, double py) => px >= X && px < Right && py >= Y && py < Bottom;
}

/// <summary>Horizontal / vertical placement of a text inside its box.</summary>
public enum LaAlign { Start, Center, End }

/// <summary>Where a liveitem text goes: its box and the alignment of the lines in it.</summary>
public readonly record struct LaTextPlacement(LaRect Box, LaAlign Horizontal, LaAlign Vertical, bool Wrap);

/// <summary>
/// The LiveArea screen layout per template style, in the 960x544 screen of the Vita: the background area, the gate
/// (startup image, 280x158) and each frame's rectangle. The tables are Vita3K's (gui/src/live_area.cpp), which give
/// positions as distances from the right and bottom screen edges; here they are converted to top-left rectangles.
/// Also computes where a liveitem's images and text go inside a frame (align/valign/x/y, origin, width/height,
/// margins), following the same rules.
/// </summary>
public static class LiveAreaLayout
{
    public const double ScreenW = 960, ScreenH = 544;
    public const double GateW = 280, GateH = 158;

    /// <summary>The background image area (840x500 at the bottom centre).</summary>
    public static readonly LaRect BackgroundRect = new(ScreenW - 900, ScreenH - 500, 840, 500);

    sealed record Style((double X, double Y) Gate, (string Id, double X, double Y, double W, double H)[] Frames);

    static readonly Dictionary<string, Style> Styles = new()
    {
        ["a1"] = new((620, 361), [("frame1", 900, 414, 260, 260), ("frame2", 320, 414, 260, 260), ("frame3", 900, 154, 840, 150)]),
        ["a2"] = new((620, 395), [("frame1", 900, 404, 260, 400), ("frame2", 320, 404, 260, 400), ("frame3", 640, 204, 320, 200)]),
        ["a3"] = new((620, 395), [("frame1", 900, 414, 260, 200), ("frame2", 320, 414, 260, 200), ("frame3", 900, 214, 260, 210),
            ("frame4", 640, 214, 320, 210), ("frame5", 320, 214, 260, 210)]),
        ["a4"] = new((620, 395), [("frame1", 900, 414, 260, 200), ("frame2", 320, 414, 260, 200), ("frame3", 900, 214, 840, 70),
            ("frame4", 900, 144, 840, 70), ("frame5", 900, 74, 840, 70)]),
        ["a5"] = new((380, 395), [("frame1", 900, 412, 480, 68), ("frame2", 900, 344, 480, 68), ("frame3", 900, 276, 480, 68),
            ("frame4", 900, 208, 480, 68), ("frame5", 900, 140, 480, 68), ("frame6", 900, 72, 480, 68), ("frame7", 420, 214, 360, 210)]),
        ["browser"] = new((381, 395), [("frame1", 890, 405, 472, 50), ("frame2", 890, 355, 472, 50), ("frame3", 890, 305, 472, 50),
            ("frame4", 890, 255, 472, 50), ("frame5", 890, 205, 472, 50), ("frame6", 890, 155, 472, 50), ("frame7", 890, 105, 472, 50),
            ("frame8", 890, 55, 472, 50), ("frame9", 381, 223, 282, 108), ("frame10", 381, 115, 282, 108)]),
        ["content_manager"] = new((620, 361), []),
        ["psmobile"] = new((380, 345), [("frame1", 866, 414, 446, 154), ("frame2", 866, 249.5, 446, 109), ("frame3", 866, 119, 196, 58),
            ("frame4", 866, 34, 772, 30)]),
    };

    /// <summary>Styles this layout knows ("a1".."a5", "browser", "content_manager", "psmobile").</summary>
    public static IEnumerable<string> KnownStyles => Styles.Keys;

    static Style StyleOf(string style) => Styles.TryGetValue(style.ToLowerInvariant(), out var s) ? s : Styles["a1"];

    public static LaRect Gate(string style)
    {
        var g = StyleOf(style).Gate;
        return new(ScreenW - g.X, ScreenH - g.Y, GateW, GateH);
    }

    /// <summary>The rectangle of a frame id in a style, or null when the style has no such frame.</summary>
    public static LaRect? Frame(string style, string frameId)
    {
        foreach (var f in StyleOf(style).Frames)
            if (string.Equals(f.Id, frameId, StringComparison.OrdinalIgnoreCase)) return new(ScreenW - f.X, ScreenH - f.Y, f.W, f.H);
        return null;
    }

    /// <summary>
    /// Where an image of natural size (w, h) goes in a frame: scaled down (keeping its aspect) when larger than the
    /// frame, centred, or pushed to an edge by align="left|right" / valign="top|bottom", moved by x (right) and y
    /// (up), and kept inside the frame.
    /// </summary>
    public static LaRect PlaceImage(LaRect frame, double w, double h, LiveAreaImage img)
    {
        if (w <= 0 || h <= 0) return frame;
        double scale = Math.Min(1, Math.Min(frame.W / w, frame.H / h));
        w *= scale; h *= scale;
        double x = img.Align switch
        {
            "left" when img.X >= 0 => 0,
            "right" when img.X <= 0 => frame.W - w,
            _ => (frame.W - w) / 2 + img.X,
        };
        double y = img.Valign switch
        {
            "top" when img.Y <= 0 => 0,
            "bottom" when img.Y >= 0 => frame.H - h,
            _ => (frame.H - h) / 2 - img.Y,
        };
        x = Math.Clamp(x, 0, Math.Max(0, frame.W - w));
        y = Math.Clamp(y, 0, Math.Max(0, frame.H - h));
        return new(frame.X + x, frame.Y + y, w, h);
    }

    /// <summary>
    /// The box and alignment of a liveitem text. The origin is the background image (origin empty or "background"),
    /// the image ("image") or the frame ("frame", or when the origin item is missing). The box has the text's
    /// width/height (else the origin's) and is placed in the origin by align (left/center/right/outside-right) and
    /// valign (top/center/bottom/outside-top), then moved by x (right) and y (up); margins are inside it. The lines are
    /// aligned in the box by text-align / text-valign (else line-align, else align / valign; centred by default).
    /// </summary>
    public static LaTextPlacement PlaceText(LaRect frame, LaRect? background, LaRect? image, LiveAreaText t)
    {
        LaRect box = t.Origin switch
        {
            "image" => image ?? background ?? frame,
            "frame" => frame,
            _ => background ?? frame,
        };
        // the text box: width/height (default: the origin's), placed in the origin by align/valign
        double w = t.Width > 0 ? t.Width : box.W;
        double h = t.Height > 0 && (t.WordScroll || t.Height <= frame.H) ? t.Height : box.H;
        double x = t.Align switch
        {
            "left" => box.X,
            "right" => box.Right - w,
            "outside-right" => box.Right,
            _ => box.X + (box.W - w) / 2,
        };
        if (t.Align == "outside-right" && t.Width <= 0) w = Math.Max(0, frame.Right - x); // up to the frame edge
        // with a y offset, top/bottom are measured from the centre (as the shell draws e.g. Silent Hill's
        // valign="top" y="47" caption on its frame image)
        string valign = t.Y != 0 && t.Valign is "top" or "bottom" ? "center" : t.Valign;
        double y = valign switch
        {
            "top" => box.Y,
            "bottom" => box.Bottom - h,
            "outside-top" => box.Y - h,
            _ => box.Y + (box.H - h) / 2,
        };
        x += t.X;
        y -= t.Y;
        // margins are inside the box
        x += t.MarginLeft; w -= t.MarginLeft + t.MarginRight;
        y += t.MarginTop; h -= t.MarginTop + t.MarginBottom;
        // lines in the box: text-align / text-valign, else line-align, else the box alignment, else centred
        string ha = t.TextAlign.Length > 0 ? t.TextAlign : t.LineAlign.Length > 0 ? t.LineAlign : t.Align;
        string va = t.TextValign.Length > 0 ? t.TextValign : valign;
        var horizontal = ha switch { "left" or "outside-right" => LaAlign.Start, "right" => LaAlign.End, _ => LaAlign.Center };
        var vertical = va switch { "top" => LaAlign.Start, "bottom" or "outside-top" => LaAlign.End, _ => LaAlign.Center };
        return new(new LaRect(x, y, Math.Max(0, w), Math.Max(0, h)), horizontal, vertical, t.WordWrap);
    }
}
