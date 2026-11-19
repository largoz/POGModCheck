using System.Collections.Generic;
using System.Linq;
using POGMods.ModCheck;
using Xunit;

public class ModListTests
{
    static ModEntry M(string id, string v = "1.0.0", string h = "aaaaaaaaaaaa", bool req = false) => new ModEntry(id, v, h, req);
    static ModEntry S(string id, string v = "1.0.0", string h = "aaaaaaaaaaaa") => new ModEntry(id, v, h, ModNeed.SameVersion);

    [Fact]
    public void RoundTripKeepsEveryField()
    {
        var mods = new[] { M("POG Timer", "1.1.0", "0123456789ab"), M("ShopDemo", "1.0.0", "", true) };
        Assert.True(ModListCodec.TryParse(ModListCodec.Serialize(mods), out var back));
        Assert.Equal(2, back.Count);
        Assert.Equal("POG Timer", back[0].Id); Assert.Equal("1.1.0", back[0].Version); Assert.Equal("0123456789ab", back[0].Hash); Assert.False(back[0].Required);
        Assert.Equal("ShopDemo", back[1].Id); Assert.Equal("", back[1].Hash); Assert.True(back[1].Required);
    }

    [Fact]
    public void EveryNeedComesBack()
    {
        var mods = new[] { M("A"), M("B", req: true), S("C") };
        Assert.True(ModListCodec.TryParse(ModListCodec.Serialize(mods), out var back));
        Assert.Equal(new[] { ModNeed.OwnOnly, ModNeed.Required, ModNeed.SameVersion }, back.Select(m => m.Need));
    }

    [Fact]
    public void TabsAndLineBreaksInsideAFieldDoNotBreakTheList()
    {
        var mods = new[] { M("Odd\tName\nHere", "1.0\r") };
        Assert.True(ModListCodec.TryParse(ModListCodec.Serialize(mods), out var back));
        Assert.Single(back);
        Assert.Equal("Odd Name Here", back[0].Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("POGMC2\nA\t1\th\t0")]
    [InlineData("POGMC1\nA\t1\th")]
    [InlineData("POGMC1\nA\t1\th\tyes")]
    [InlineData("POGMC1\nA\t1\th\t3")]
    [InlineData("POGMC1\nA\t1\th\t12")]
    [InlineData("POGMC1\n\t1\th\t0")]
    public void SomethingElseIsRejected(string text) => Assert.False(ModListCodec.TryParse(text, out _));

    [Fact]
    public void AnEmptyListIsValid()
    {
        Assert.True(ModListCodec.TryParse(ModListCodec.Serialize(new ModEntry[0]), out var back));
        Assert.Empty(back);
    }

    [Fact]
    public void SameRequiredModsMatch()
    {
        var a = new[] { M("ShopDemo", req: true), M("POG Timer") };
        Assert.True(ModCompare.Compare(a, a).IsEmpty);
    }

    [Fact]
    public void ARequiredModTheOtherHasIsMissing()
    {
        var mine = new[] { M("POG Timer") };
        var theirs = new[] { M("ShopDemo", req: true), M("POGContentLib", "0.1.0", req: true) };
        var d = ModCompare.Compare(mine, theirs);
        Assert.Equal(new[] { "POGContentLib", "ShopDemo" }, d.Missing.Select(m => m.Id));
        Assert.Empty(d.Extra); Assert.Empty(d.Different);
    }

    [Fact]
    public void ARequiredModOnlyIHaveIsExtra()
    {
        var d = ModCompare.Compare(new[] { M("ShopDemo", req: true) }, new ModEntry[0]);
        Assert.Single(d.Extra); Assert.Empty(d.Missing);
    }

    [Fact]
    public void OwnOnlyModsAreNeverReported()
    {
        var mine = new[] { M("POG Graphics", "1.0"), M("POG Timer", "1.0.2", "111111111111") };
        var theirs = new[] { M("POG Timer", "1.1.0", "222222222222"), M("POG Config") };
        Assert.True(ModCompare.Compare(mine, theirs).IsEmpty);
    }

    [Fact]
    public void ARequiredModWithAnotherVersionIsDifferent()
    {
        var d = ModCompare.Compare(new[] { M("ShopDemo", "1.0.0", req: true) }, new[] { M("ShopDemo", "1.1.0", req: true) });
        Assert.Single(d.Different);
        Assert.Contains("Different version: ShopDemo 1.0.0, the host has 1.1.0 (everyone needs the same one)", d.Lines("the host"));
    }

    [Fact]
    public void ASameVersionWithAnotherBuildIsDifferent()
    {
        var d = ModCompare.Compare(new[] { M("ShopDemo", "1.0.0", "aaaaaaaaaaaa", true) }, new[] { M("ShopDemo", "1.0.0", "bbbbbbbbbbbb", true) });
        Assert.Single(d.Different);
        Assert.StartsWith("Different build:", d.Lines("the host")[0]);
    }

    [Fact]
    public void AnUnknownHashIsNotABuildDifference()
    {
        var d = ModCompare.Compare(new[] { M("ShopDemo", "1.0.0", "", true) }, new[] { M("ShopDemo", "1.0.0", "bbbbbbbbbbbb", true) });
        Assert.True(d.IsEmpty);
    }

    [Fact]
    public void RequiredOnOneSideIsEnoughToCompareVersions()
    {
        var d = ModCompare.Compare(new[] { M("Some Pack", "2.0") }, new[] { M("Some Pack", "1.0", req: true) });
        Assert.Single(d.Different);
    }

    [Fact]
    public void NamesMatchIgnoringCaseSpacesAndPunctuation()
    {
        var d = ModCompare.Compare(new[] { M("POG Custom Items", req: true) }, new[] { M("pogcustomitems", req: true) });
        Assert.True(d.IsEmpty);
        Assert.Equal("pogcustomitems", ModEntry.KeyOf("POG-Custom Items"));
    }

    [Fact]
    public void TheReportNamesEachKind()
    {
        var mine = new[] { M("A", req: true), M("C", "1", req: true) };
        var theirs = new[] { M("B", req: true), M("C", "2", req: true) };
        var lines = ModCompare.Compare(mine, theirs).Lines("the host");
        Assert.Equal(3, lines.Count);
        Assert.StartsWith("Missing: B", lines[0]);
        Assert.StartsWith("Extra: A", lines[1]);
        Assert.StartsWith("Different version: C 1", lines[2]);
    }

    // ---- same version when both have it (a timer the host sends to the others)

    [Fact]
    public void ASameVersionModOnlyOneSideHasIsNotReported()
    {
        Assert.True(ModCompare.Compare(new[] { S("POG Timer") }, new ModEntry[0]).IsEmpty);
        Assert.True(ModCompare.Compare(new ModEntry[0], new[] { S("POG Timer") }).IsEmpty);
    }

    [Fact]
    public void ASameVersionModBothHaveInAnotherVersionIsDifferent()
    {
        var d = ModCompare.Compare(new[] { S("POG Timer", "1.0.2") }, new[] { S("POG Timer", "1.1.0") });
        Assert.Single(d.Different);
        Assert.Equal("Different version: POG Timer 1.0.2, the host has 1.1.0 (whoever has it needs the same one)", d.Lines("the host")[0]);
    }

    [Fact]
    public void AMarkOnOneSideIsEnoughToCompare()
    {
        // An older copy without the mark against a newer one with it: still compared.
        Assert.Single(ModCompare.Compare(new[] { M("POG Timer", "1.1.0") }, new[] { S("POG Timer", "1.1.1") }).Different);
        Assert.Single(ModCompare.Compare(new[] { S("POG Timer", "1.1.1") }, new[] { M("POG Timer", "1.1.0") }).Different);
    }

    [Fact]
    public void TheSameCopyOfASameVersionModMatches() =>
        Assert.True(ModCompare.Compare(new[] { S("POG Timer", "1.1.0") }, new[] { M("POG Timer", "1.1.0") }).IsEmpty);
}
