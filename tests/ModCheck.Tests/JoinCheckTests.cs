using System;
using System.Collections.Generic;
using System.Linq;
using POGMods.ModCheck;
using Xunit;

public class JoinOfferTests
{
    static ModEntry M(string id, string v = "1.0.0", string h = "aaaaaaaaaaaa", bool req = false) => new ModEntry(id, v, h, req);
    static ModEntry S(string id, string v = "1.0.0", string h = "aaaaaaaaaaaa") => new ModEntry(id, v, h, ModNeed.SameVersion);

    [Fact]
    public void RoundTripKeepsModeTagsDigestsAndNeeds()
    {
        var mods = new[] { M("POG Timer"), M("ShopDemo", req: true), S("POG Custom Items", "0.3.0") };
        var offer = JoinOffer.From(mods, JoinOffer.Kind.Ask);
        Assert.True(JoinOffer.TryParse(offer.Serialize(), out var back));
        Assert.Equal(JoinOffer.Kind.Ask, back.Mode);
        Assert.Equal(mods.Select(ModDigest.Of).OrderBy(x => x), back.Digests.OrderBy(x => x));
        foreach (var m in mods)
        {
            var copy = Assert.Single(back.WithTag(ModDigest.TagOf(m.Key)));
            Assert.Equal(ModDigest.Of(m), copy.Digest);
            Assert.Equal(m.Need, copy.Need);
        }
    }

    [Theory]
    [InlineData(JoinOffer.Kind.Ask)]
    [InlineData(JoinOffer.Kind.Accept)]
    [InlineData(JoinOffer.Kind.Unchecked)]
    public void EveryModeComesBack(JoinOffer.Kind mode)
    {
        Assert.True(JoinOffer.TryParse(JoinOffer.From(new[] { M("A") }, mode).Serialize(), out var back));
        Assert.Equal(mode, back.Mode);
    }

    [Fact]
    public void AnOfferWithNoModsIsValid()
    {
        Assert.True(JoinOffer.TryParse(JoinOffer.From(new ModEntry[0], JoinOffer.Kind.Ask).Serialize(), out var back));
        Assert.Empty(back.Mods);
    }

    [Fact]
    public void ANameTwiceCountsOnceAsInTheComparison()
    {
        var offer = JoinOffer.From(new[] { M("Timer", "1.0.0"), M("timer", "2.0.0") }, JoinOffer.Kind.Ask);
        var copy = Assert.Single(offer.Mods);
        Assert.Equal(ModDigest.Of(M("Timer", "1.0.0")), copy.Digest);
    }

    [Fact]
    public void TheOfferHoldsNoneOfTheGamesSeparatorsNorNames()
    {
        var mods = new[] { M("Odd;Name|Here", "1;0|1", "ab;cd", true), M("Other;One") };
        string s = JoinOffer.From(mods, JoinOffer.Kind.Ask).Serialize();
        Assert.DoesNotContain(";", s);
        Assert.DoesNotContain("|", s);
        Assert.DoesNotContain("Odd", s);
    }

    [Fact]
    public void AnOfferTooLongForTheRequestBecomesUnchecked()
    {
        var mods = Enumerable.Range(0, 200).Select(i => M("Mod" + i)).ToList();
        string s = JoinOffer.From(mods, JoinOffer.Kind.Ask).SerializeWithin(600);
        Assert.True(s.Length <= 600);
        Assert.True(JoinOffer.TryParse(s, out var back));
        Assert.Equal(JoinOffer.Kind.Unchecked, back.Mode);
    }

    [Fact]
    public void FortyModsFitTheRequest()
    {
        var mods = Enumerable.Range(0, 40).Select(i => M("Mod" + i)).ToList();
        Assert.True(JoinOffer.From(mods, JoinOffer.Kind.Ask).Serialize().Length <= 600);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("POGMC1\nX\t1\t\t0")]
    [InlineData("POGMC2X\u001e")]
    [InlineData("POGMC2Q")]
    [InlineData("POGMC2Q\u001e\u001e")]
    [InlineData("POGMC2Q\u001eabcd0123456zr")]
    [InlineData("POGMC2Q\u001eabcd01234567x")]
    [InlineData("POGMC2Q\u001eabcd01234567")]
    [InlineData("POGMC2Q\u001eABCD01234567r")]
    [InlineData("POGMC2Q\u001eabcd01234567r,")]
    public void BrokenOffersAreRefused(string text) => Assert.False(JoinOffer.TryParse(text, out _));
}

public class ConnectionPayloadTests
{
    const string Offer = "POGMC2Q\u001eabcd01234567r";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void EmptyDataGetsTheGamesPrefix(string text) => Assert.Equal("POGC1|" + Offer, ConnectionPayload.WithOffer(text, Offer));

    [Fact]
    public void TheGamesKeysStayAndAnOlderOfferIsReplaced()
    {
        string once = ConnectionPayload.WithOffer("POGC1|mines;caves", Offer);
        Assert.Equal("POGC1|mines;caves;" + Offer, once);
        string twice = ConnectionPayload.WithOffer(once.Replace(Offer, "POGMC1old"), Offer);
        Assert.Equal("POGC1|mines;caves;" + Offer, twice);
        Assert.Equal("POGC1|" + Offer, ConnectionPayload.WithOffer("POGC1|", Offer));
    }

    [Fact]
    public void DataInAnUnknownFormIsLeftAlone() => Assert.Null(ConnectionPayload.WithOffer("something else", Offer));

    [Fact]
    public void TheOfferIsFoundAmongTheGamesKeys()
    {
        Assert.Equal(Offer, ConnectionPayload.FindOffer("POGC1|mines;" + Offer + ";caves"));
        Assert.Null(ConnectionPayload.FindOffer("POGC1|mines;caves"));
        Assert.Null(ConnectionPayload.FindOffer(null));
        Assert.Null(ConnectionPayload.FindOffer(Offer));
    }

    // The game splits its data on ';' after the prefix and keeps every non-empty key (NetworkContent.ParsePayload).
    [Fact]
    public void TheGameStillReadsItsOwnKeys()
    {
        string data = ConnectionPayload.WithOffer("POGC1|mines;caves", Offer);
        var keys = data.Substring("POGC1|".Length).Split(';').Where(k => k.Length > 0).ToList();
        Assert.Contains("mines", keys);
        Assert.Contains("caves", keys);
        Assert.Equal(3, keys.Count);
    }
}

public class JoinCheckTests
{
    static ModEntry M(string id, string v = "1.0.0", string h = "aaaaaaaaaaaa", bool req = false) => new ModEntry(id, v, h, req);
    static ModEntry S(string id, string v = "1.0.0", string h = "aaaaaaaaaaaa") => new ModEntry(id, v, h, ModNeed.SameVersion);
    static JoinOffer Offer(params ModEntry[] mods) => JoinOffer.From(mods, JoinOffer.Kind.Ask);

    [Fact]
    public void SameModsDoNotDiffer() =>
        Assert.False(JoinCheck.Differs(new[] { M("ShopDemo", req: true), M("Timer") }, Offer(M("ShopDemo", req: true), M("Timer"))));

    [Fact]
    public void OwnOnlyModsMayDifferFreely() =>
        Assert.False(JoinCheck.Differs(new[] { M("Timer", "1.0"), M("Fonts") }, Offer(M("Timer", "2.0"), M("Brightness"))));

    [Fact]
    public void APlayerWithoutAModIRequireDiffers() =>
        Assert.True(JoinCheck.Differs(new[] { M("ShopDemo", req: true) }, Offer(M("Timer"))));

    [Fact]
    public void APlayerWithAnotherVersionOfAModIRequireDiffers() =>
        Assert.True(JoinCheck.Differs(new[] { M("ShopDemo", "1.0.0", req: true) }, Offer(M("ShopDemo", "1.1.0", req: true))));

    [Fact]
    public void APlayerRequiringAModIDoNotHaveDiffers() =>
        Assert.True(JoinCheck.Differs(new[] { M("Timer") }, Offer(M("ShopDemo", req: true))));

    [Fact]
    public void APlayerRequiringAModIHaveInAnotherVersionDiffers() =>
        Assert.True(JoinCheck.Differs(new[] { M("ShopDemo", "1.0.0") }, Offer(M("ShopDemo", "1.1.0", req: true))));

    [Fact]
    public void AModIRequireThatThePlayerHasAsOwnOnlyIsFine() =>
        Assert.False(JoinCheck.Differs(new[] { M("ShopDemo", req: true) }, Offer(M("ShopDemo"))));

    [Fact]
    public void MySameVersionModThePlayerLacksIsFine() =>
        Assert.False(JoinCheck.Differs(new[] { S("POG Timer", "1.1.1") }, Offer(M("ShopDemo"))));

    [Fact]
    public void MySameVersionModThePlayerHasInAnotherVersionDiffers() =>
        Assert.True(JoinCheck.Differs(new[] { S("POG Timer", "1.1.1") }, Offer(M("POG Timer", "1.1.0"))));

    [Fact]
    public void ThePlayersSameVersionModIHaveInAnotherVersionDiffers() =>
        Assert.True(JoinCheck.Differs(new[] { M("POG Timer", "1.1.0") }, Offer(S("POG Timer", "1.1.1"))));

    [Fact]
    public void TheSameCopyOfASameVersionModIsFine() =>
        Assert.False(JoinCheck.Differs(new[] { S("POG Timer", "1.1.1") }, Offer(S("POG Timer", "1.1.1"))));
}

public class JoinRefusalTests
{
    static ModEntry M(string id, string v = "1.0.0", string h = "aaaaaaaaaaaa", bool req = false) => new ModEntry(id, v, h, req);
    static ModEntry S(string id, string v = "1.0.0", string h = "aaaaaaaaaaaa") => new ModEntry(id, v, h, ModNeed.SameVersion);

    [Fact]
    public void RoundTrip()
    {
        var entries = new[] { M("ShopDemo", req: true), M("POG Custom Items", "0.3.0", "bbbbbbbbbbbb"), S("POG Timer", "1.1.1") };
        string reason = JoinRefusal.Build(entries, 1000);
        Assert.StartsWith(JoinRefusal.Prefix, reason);
        Assert.True(JoinRefusal.TryParse(reason, out var back));
        Assert.Equal(entries.Select(e => e.ToString()), back.Select(e => e.ToString()));
    }

    [Fact]
    public void TooLongDropsTheHashesThenGivesUp()
    {
        var entries = Enumerable.Range(0, 8).Select(i => M("Required Mod " + i, "1.0." + i, "0123456789ab", true)).ToList();
        string full = JoinRefusal.Build(entries, 10000);
        string bare = JoinRefusal.Build(entries, full.Length - 1);
        Assert.NotNull(bare);
        Assert.True(JoinRefusal.TryParse(bare, out var back));
        Assert.All(back, e => Assert.Equal("", e.Hash));
        Assert.Null(JoinRefusal.Build(entries, 40));
    }

    [Fact]
    public void OnlyWhatTheComparisonNeedsIsSent()
    {
        var host = new[] { M("ShopDemo", req: true), S("POG Timer", "1.1.1"), M("POG Graphics", "2.0"), S("POG Stones") };
        var player = JoinOffer.From(new[] { M("ShopDemo", req: true), M("POG Timer", "1.1.0"), M("POG Graphics", "1.0") }, JoinOffer.Kind.Ask);
        // ShopDemo: the same copy, but the player requires it (else it would look missing on the host);
        // POG Timer: differs where it matters; POG Graphics: own only both sides; POG Stones: the player lacks it.
        Assert.Equal(new[] { "POG Timer", "ShopDemo" }, JoinRefusal.EntriesFor(host, player).Select(e => e.Id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("POG_CONTENT:mines")]
    [InlineData("Lost connection")]
    public void OtherReasonsAreNotARefusal(string reason) => Assert.False(JoinRefusal.TryParse(reason, out _));
}

public class ModTableTests
{
    static ModEntry M(string id, string v = "1.0.0", string h = "aaaaaaaaaaaa", bool req = false) => new ModEntry(id, v, h, req);
    static ModEntry S(string id, string v = "1.0.0", string h = "aaaaaaaaaaaa") => new ModEntry(id, v, h, ModNeed.SameVersion);

    // The player's mods and the host's: ShopDemo only on the host, Spawner only on the player, Timer in two versions,
    // Stones in one version built twice.
    static readonly ModEntry[] Player = { M("Spawner", "0.3.0", req: true), S("POG Timer", "1.0.2"), M("Stones", "2.0.0", "111111111111", true) };
    static readonly ModEntry[] Host = { M("ShopDemo", req: true), S("POG Timer", "1.1.0"), M("Stones", "2.0.0", "222222222222", true) };

    [Fact]
    public void ThePlayerSeesTheHostFirstAndItselfSecond()
    {
        var t = ModTable.ForPlayer(ModCompare.Compare(Player, Host));
        Assert.Equal(("Host", "You"), (t.HostHeader, t.PlayerHeader));
        Assert.Equal(new[]
        {
            "ShopDemo | 1.0.0 | —",
            "Spawner | — | 0.3.0",
            "POG Timer | 1.1.0 | 1.0.2",
            "Stones | 2.0.0 (222222) | 2.0.0 (111111)",
        }, t.Rows.Select(r => r.ToString()));
        Assert.Equal(new[] { DiffKind.HostOnly, DiffKind.PlayerOnly, DiffKind.Version, DiffKind.Build }, t.Rows.Select(r => r.Kind));
    }

    [Fact]
    public void TheHostSeesTheSameColumns()
    {
        var player = ModTable.ForPlayer(ModCompare.Compare(Player, Host));
        var host = ModTable.ForHost(ModCompare.Compare(Host, Player), "Player 2");
        Assert.Equal(("You (host)", "Player 2"), (host.HostHeader, host.PlayerHeader));
        Assert.Equal(player.Rows.Select(r => r.ToString()).OrderBy(x => x), host.Rows.Select(r => r.ToString()).OrderBy(x => x));
        Assert.Equal(player.Rows.Select(r => r.Kind).OrderBy(x => x), host.Rows.Select(r => r.Kind).OrderBy(x => x));
    }

    [Fact]
    public void TheRichTextHasAColumnPerSideAndCutsALongList()
    {
        var t = ModTable.ForPlayer(ModCompare.Compare(Player, Host));
        var style = new ModTable.RichStyle { HostAt = 50, PlayerAt = 75, Dim = "#aaa", Absent = "#f00", Cell = "#ff0" };
        string all = t.RichText(10, style);
        var lines = all.Split('\n');
        Assert.Equal(5, lines.Length);
        Assert.All(lines.Skip(1), l => Assert.Equal(2, CountOf(l, "<pos=")));
        Assert.Contains("<color=#f00>—</color>", lines[1]);
        Assert.StartsWith("<align=left>", all);

        var cut = t.RichText(3, style).Split('\n');
        Assert.Equal(4, cut.Length); // the header, two rows, "+2 more"
        Assert.Contains("+2 more", cut[3]);
    }

    [Fact]
    public void AModsNameCannotOpenATag()
    {
        var t = ModTable.ForPlayer(ModCompare.Compare(new ModEntry[0], new[] { M("<b>Bold</b>", req: true) }));
        string text = t.RichText(5, new ModTable.RichStyle { NameChars = 40 });
        Assert.DoesNotContain("<b>", text, StringComparison.Ordinal); // a culture-aware search skips the zero-width space
        Assert.Contains("<\u200Bb>Bold<\u200B/b>", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("POG Custom Items", 10, "POG Cust..")]
    [InlineData("Short", 10, "Short")]
    [InlineData("Exactly10!", 10, "Exactly10!")]
    public void LongTextIsCut(string text, int chars, string expected) => Assert.Equal(expected, ModTable.Cut(text, chars));

    [Fact]
    public void TheQuestionHasTwoLines() => Assert.Equal(2, ModDiff.PopupQuestion.Split('\n').Length);

    static int CountOf(string s, string part) { int n = 0, i = 0; while ((i = s.IndexOf(part, i, StringComparison.Ordinal)) >= 0) { n++; i++; } return n; }
}

/// <summary>
/// The two promises the check before loading rests on, over many generated pairs of mod sets:
/// the host never lets a player in silently when <see cref="ModCompare"/> would report a difference, and the
/// refusal gives the player the same report as the host's whole list would.
/// </summary>
public class JoinCheckAgreementTests
{
    static readonly string[] Names = { "ShopDemo", "POG Custom Items", "POGContentLib", "POG Timer", "POG Fonts", "Bright", "Stones", "Spawner" };
    static readonly string[] Versions = { "1.0.0", "1.1.0" };
    static readonly string[] Hashes = { "", "aaaaaaaaaaaa", "bbbbbbbbbbbb" };
    static readonly ModNeed[] Needs = { ModNeed.OwnOnly, ModNeed.OwnOnly, ModNeed.SameVersion, ModNeed.Required };

    static List<ModEntry> Side(Random r)
    {
        var mods = new List<ModEntry>();
        foreach (var n in Names)
            if (r.Next(3) > 0) mods.Add(new ModEntry(n, Versions[r.Next(Versions.Length)], Hashes[r.Next(Hashes.Length)], Needs[r.Next(Needs.Length)]));
        return mods;
    }

    static string Report(ModDiff d) => string.Join("\n", d.Lines("the host"));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void HostAndPlayerAgree(int seed)
    {
        var r = new Random(seed);
        int differing = 0, silent = 0, overcautious = 0, sameVersionRows = 0;
        for (int i = 0; i < 3000; i++)
        {
            var host = Side(r);
            var player = Side(r);
            var offer = JoinOffer.From(player, JoinOffer.Kind.Ask);
            var exact = ModCompare.Compare(player, host);
            bool differs = JoinCheck.Differs(host, offer);

            if (!exact.IsEmpty) Assert.True(differs, $"case {i}: the host would let in silently: {Report(exact)}");
            if (!differs) { silent++; continue; }
            differing++;
            if (exact.Different.Any(p => p.Mine.Need != ModNeed.Required && p.Theirs.Need != ModNeed.Required)) sameVersionRows++;

            string reason = JoinRefusal.Build(JoinRefusal.EntriesFor(host, offer), 100000);
            Assert.True(JoinRefusal.TryParse(reason, out var fromRefusal));
            var seen = ModCompare.Compare(player, fromRefusal);
            Assert.Equal(Report(exact), Report(seen));
            if (seen.IsEmpty) overcautious++;
        }
        // The generator must reach every branch, or the test proves nothing.
        Assert.True(differing > 100 && silent > 20 && overcautious > 20 && sameVersionRows > 50,
            $"differing {differing}, silent {silent}, overcautious {overcautious}, same-version rows {sameVersionRows}");
    }
}
