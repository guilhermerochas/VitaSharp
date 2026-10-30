using MauiReactor;

namespace VitaSharp.Android.Ui;

/// <summary>The palette of docs/android/mockups: dark surfaces, one blue accent.</summary>
public static class Palette
{
    public static readonly Color Background = Color.FromArgb("#0E1116");
    public static readonly Color Surface = Color.FromArgb("#171B22");
    public static readonly Color SurfaceHigh = Color.FromArgb("#1E2A40");
    public static readonly Color Highlight = Color.FromArgb("#1A2233");
    public static readonly Color HighlightBorder = Color.FromArgb("#2B3A55");
    public static readonly Color Border = Color.FromArgb("#232A35");
    public static readonly Color BorderStrong = Color.FromArgb("#2A313D");
    public static readonly Color Text = Color.FromArgb("#E8ECF2");
    public static readonly Color TextSoft = Color.FromArgb("#C9D1DC");
    public static readonly Color Muted = Color.FromArgb("#9AA4B2");
    public static readonly Color Faint = Color.FromArgb("#6B7584");
    public static readonly Color Accent = Color.FromArgb("#6E9BFF");
    public static readonly Color AccentSoft = Color.FromArgb("#9DB4E8");
    public static readonly Color OnAccent = Color.FromArgb("#0B1220");
    public static readonly Color Track = Color.FromArgb("#2A3550");
    public static readonly Color Gold = Color.FromArgb("#E2B13C");
    public static readonly Color Danger = Color.FromArgb("#FF8A8A");
    public static readonly Color Ok = Color.FromArgb("#7FD6A4");
}

/// <summary>Font aliases registered in MauiProgram (Sora for headings, Inter for text).</summary>
public static class Fonts
{
    public const string Inter = "Inter", InterMedium = "InterMedium", InterSemiBold = "InterSemiBold";
    public const string SoraSemiBold = "SoraSemiBold", SoraBold = "SoraBold";
}

/// <summary>Default styles: every page dark, every label Inter in the text colour.</summary>
public sealed class VitaTheme : Theme
{
    protected override void OnApply()
    {
        ContentPageStyles.Default = _ => _.BackgroundColor(Palette.Background);
        NavigationPageStyles.Default = _ => _.BarBackgroundColor(Palette.Background).BarTextColor(Palette.Text);
        LabelStyles.Default = _ => _.FontFamily(Fonts.Inter).TextColor(Palette.Text).FontSize(14);
        ButtonStyles.Default = _ => _.FontFamily(Fonts.InterSemiBold).FontSize(15).CornerRadius(16)
            .BackgroundColor(Palette.Accent).TextColor(Palette.OnAccent).Padding(18, 12);
        EntryStyles.Default = _ => _.FontFamily(Fonts.Inter).FontSize(15).TextColor(Palette.Text).PlaceholderColor(Palette.Muted);
        SwitchStyles.Default = _ => _.OnColor(Palette.Accent).ThumbColor(Palette.Text);
        ProgressBarStyles.Default = _ => _.ProgressColor(Palette.Accent);
        ActivityIndicatorStyles.Default = _ => _.Color(Palette.Accent);
    }
}
