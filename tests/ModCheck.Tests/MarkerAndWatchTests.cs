using System.Collections.Generic;
using POGMods.ModCheck;
using Xunit;

// Planted on purpose: this test assembly marks itself the way a mod author would, so the real reflection read has a
// known answer. Removing this line must fail TheMarkThisAssemblyCarriesIsRead.
[assembly: System.Reflection.AssemblyMetadata("POGModCheck", "required")]

public class ModMarkerTests
{
    static KeyValuePair<string, string> P(string k, string v) => new KeyValuePair<string, string>(k, v);

    [Fact]
    public void TheMarkThisAssemblyCarriesIsRead()
    {
        Assert.Equal(ModMarker.Mark.Required, ModMarker.Read(typeof(ModMarkerTests).Assembly, out var unknown));
        Assert.Null(unknown);
    }

    [Fact]
    public void AnAssemblyWithOtherMetadataOnlyIsUnmarked()
    {
        // The test SDK writes its own AssemblyMetadata pairs into some assemblies; none uses this key.
        Assert.Equal(ModMarker.Mark.None, ModMarker.Read(typeof(object).Assembly, out _));
        Assert.Equal(ModMarker.Mark.None, ModMarker.Read(typeof(FactAttribute).Assembly, out _));
        Assert.Equal(ModMarker.Mark.None, ModMarker.Read(null, out _));
    }

    [Fact]
    public void CaseAndSpacesDoNotMatter()
    {
        Assert.Equal(ModMarker.Mark.Required, ModMarker.Interpret(new[] { P(" pogmodcheck ", " Required ") }, out _));
    }

    [Fact]
    public void AnotherValueIsUnknownAndGivenBack()
    {
        Assert.Equal(ModMarker.Mark.Unknown, ModMarker.Interpret(new[] { P("POGModCheck", "maybe") }, out var unknown));
        Assert.Equal("maybe", unknown);
    }

    [Fact]
    public void RequiredWinsOverAnotherValue()
    {
        Assert.Equal(ModMarker.Mark.Required, ModMarker.Interpret(new[] { P("POGModCheck", "maybe"), P("POGModCheck", "required") }, out var unknown));
        Assert.Null(unknown);
    }

    [Theory]
    [InlineData("same-version")]
    [InlineData(" Same Version ")]
    [InlineData("SAMEVERSION")]
    public void SameVersionIsKnownInAnySpelling(string value)
    {
        Assert.Equal(ModMarker.Mark.SameVersion, ModMarker.Interpret(new[] { P("POGModCheck", value) }, out var unknown));
        Assert.Null(unknown);
    }

    [Fact]
    public void RequiredWinsOverSameVersionWhichWinsOverAnotherValue()
    {
        Assert.Equal(ModMarker.Mark.Required, ModMarker.Interpret(new[] { P("POGModCheck", "same-version"), P("POGModCheck", "required") }, out _));
        Assert.Equal(ModMarker.Mark.SameVersion, ModMarker.Interpret(new[] { P("POGModCheck", "maybe"), P("POGModCheck", "same-version") }, out var unknown));
        Assert.Null(unknown);
    }

    [Fact]
    public void OtherKeysAreIgnored()
    {
        Assert.Equal(ModMarker.Mark.None, ModMarker.Interpret(new[] { P("RepositoryUrl", "required"), P(null, null) }, out _));
        Assert.Equal(ModMarker.Mark.None, ModMarker.Interpret(null, out _));
    }
}

public class PeerWatchTests
{
    const ulong Host = 0;

    [Fact]
    public void APlayerThatSendsItsListIsNeverNamed()
    {
        var w = new PeerWatch(Host, 15f);
        w.Present(new ulong[] { 0, 1 }, 0f);
        w.Answered(1);
        Assert.Empty(w.Overdue(100f));
    }

    [Fact]
    public void ASilentPlayerIsNamedOnceAfterTheWait()
    {
        var w = new PeerWatch(Host, 15f);
        w.Present(new ulong[] { 0, 1 }, 10f);
        Assert.Empty(w.Overdue(24f));
        Assert.Equal(new ulong[] { 1 }, w.Overdue(26f));
        Assert.Empty(w.Overdue(60f));
    }

    [Fact]
    public void APlayerThatLeftBeforeTheWaitIsNotNamed()
    {
        var w = new PeerWatch(Host, 15f);
        w.Present(new ulong[] { 0, 1 }, 0f);
        w.Present(new ulong[] { 0 }, 5f);
        Assert.Empty(w.Overdue(30f));
    }

    [Fact]
    public void AListThatCameBeforeTheJoinCounts()
    {
        var w = new PeerWatch(Host, 15f);
        w.Answered(2);
        w.Present(new ulong[] { 0, 2 }, 0f);
        Assert.Empty(w.Overdue(30f));
    }

    [Fact]
    public void TheHostIsNotWatched()
    {
        var w = new PeerWatch(Host, 15f);
        w.Present(new ulong[] { 0 }, 0f);
        Assert.Empty(w.Overdue(30f));
    }

    [Fact]
    public void EachPlayerWaitsFromItsOwnJoin()
    {
        var w = new PeerWatch(Host, 15f);
        w.Present(new ulong[] { 0, 1 }, 0f);
        w.Present(new ulong[] { 0, 1, 2 }, 10f);
        Assert.Equal(new ulong[] { 1 }, w.Overdue(16f));
        Assert.Empty(w.Overdue(20f));
        Assert.Equal(new ulong[] { 2 }, w.Overdue(26f));
    }
}
