using VitaSharp.Core.Host;

namespace VitaSharp.Tests;

/// <summary>The editing model of the desktop IME overlay: max length, input types, caret and surrogate pairs.</summary>
public sealed class TextEntryTests
{
    [Fact]
    public void TypingStopsAtMaxLength()
    {
        var e = new TextEntry("Nome", "Yu", 6, 0, false, false);
        Assert.Equal(2, e.Caret);
        Assert.True(e.Insert(" Narukami"));
        Assert.Equal("Yu Nar", e.Text);
        Assert.False(e.Insert("x"));
        Assert.True(e.Backspace());
        Assert.Equal("Yu Na", e.Text);
    }

    [Fact]
    public void EditingAtTheCaret()
    {
        var e = new TextEntry("", "ac", 16, 0, false, false);
        Assert.True(e.Move(-1));
        Assert.True(e.Insert("b"));
        Assert.Equal(("abc", 2), (e.Text, e.Caret));
        Assert.True(e.MoveTo(0));
        Assert.False(e.Backspace());
        Assert.True(e.Delete());
        Assert.Equal("bc", e.Text);
        Assert.False(e.Insert("\n"));         // single line
        e.Set("hello", 99);
        Assert.Equal(5, e.Caret);
    }

    [Fact]
    public void SurrogatePairsStayWhole()
    {
        var e = new TextEntry("", "", 3, 0, false, false);
        Assert.True(e.Insert("a😀b"));        // "a😀" fits (3 units), 'b' does not
        Assert.Equal("a😀", e.Text);
        Assert.True(e.Move(-1));
        Assert.Equal(1, e.Caret);                     // jumps over the whole pair
        Assert.True(e.Delete());
        Assert.Equal("a", e.Text);
        var f = new TextEntry("", "", 2, 0, false, false);
        Assert.True(f.Insert("x😀"));         // the pair would need units 2-3: dropped
        Assert.Equal("x", f.Text);
    }

    [Fact]
    public void InputTypesFilterCharacters()
    {
        var num = new TextEntry("", "", 8, TextEntry.TypeNumber, false, false);
        num.Insert("a1b2.3");
        Assert.Equal("123", num.Text);
        var latin = new TextEntry("", "", 8, TextEntry.TypeBasicLatin, false, false);
        latin.Insert("Olá Yu");
        Assert.Equal("Ol Yu", latin.Text);
        var multi = new TextEntry("", "", 8, 0, true, false);
        multi.Insert("a\r\nb");
        Assert.Equal("a\nb", multi.Text);
    }
}
