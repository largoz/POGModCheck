using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace POGMods.ModCheck
{
    /// <summary>How much one mod matters to the other players. The numbers order them: the stronger need of two
    /// copies decides why a difference matters.</summary>
    public enum ModNeed
    {
        /// <summary>What one player runs is nobody else's business (graphics, a UI): never compared.</summary>
        OwnOnly = 0,
        /// <summary>Nobody has to have it, but two players who both have it need the same copy (a timer the host
        /// sends to the others, say).</summary>
        SameVersion = 1,
        /// <summary>Every player needs it, the same copy (it adds network content, say).</summary>
        Required = 2,
    }

    /// <summary>One mod as one player has it.</summary>
    public sealed class ModEntry
    {
        /// <summary>The mod's name as MelonLoader shows it (MelonInfo).</summary>
        public string Id { get; }
        public string Version { get; }
        /// <summary>First 12 hex digits of the SHA-256 of the mod's DLL ("" when unknown).</summary>
        public string Hash { get; }
        public ModNeed Need { get; }
        /// <summary>Every player needs it.</summary>
        public bool Required => Need == ModNeed.Required;
        /// <summary>Compared with the other player's copy when both have it.</summary>
        public bool Compared => Need != ModNeed.OwnOnly;

        public ModEntry(string id, string version, string hash, ModNeed need)
        {
            Id = id ?? ""; Version = version ?? ""; Hash = hash ?? ""; Need = need;
        }

        public ModEntry(string id, string version, string hash, bool required)
            : this(id, version, hash, required ? ModNeed.Required : ModNeed.OwnOnly) { }

        /// <summary>Ids match ignoring case, spaces and punctuation ("POG Custom Items" = "POGCustomItems").</summary>
        public string Key => KeyOf(Id);

        public static string KeyOf(string id)
        {
            if (string.IsNullOrEmpty(id)) return "";
            var sb = new StringBuilder(id.Length);
            foreach (char c in id) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        public override string ToString() => $"{Id} {Version}" + (Hash.Length > 0 ? $" ({Hash})" : "")
            + (Need == ModNeed.Required ? " [required]" : Need == ModNeed.SameVersion ? " [same version]" : "");
    }

    /// <summary>
    /// The mod list on the wire: <c>POGMC1</c>, then one line per mod, <c>id \t version \t hash \t need</c>, the
    /// need being 0 (own only), 1 (every player) or 2 (the same version when both have it). Tabs and line breaks
    /// inside a field are replaced by spaces. Format "1": a change gets a new magic.
    /// </summary>
    public static class ModListCodec
    {
        public const string Magic = "POGMC1";

        public static string Serialize(IEnumerable<ModEntry> mods)
        {
            var sb = new StringBuilder(Magic);
            foreach (var m in mods ?? Enumerable.Empty<ModEntry>())
                sb.Append('\n').Append(Clean(m.Id)).Append('\t').Append(Clean(m.Version)).Append('\t')
                  .Append(Clean(m.Hash)).Append('\t').Append(Flag(m.Need));
            return sb.ToString();
        }

        public static bool TryParse(string text, out List<ModEntry> mods)
        {
            mods = new List<ModEntry>();
            if (string.IsNullOrEmpty(text)) return false;
            var lines = text.Split('\n');
            if (lines[0] != Magic) return false;
            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].Length == 0) continue;
                var f = lines[i].Split('\t');
                if (f.Length != 4 || f[0].Length == 0 || f[3].Length != 1 || !TryNeed(f[3][0], out var need)) return false;
                mods.Add(new ModEntry(f[0], f[1], f[2], need));
            }
            return true;
        }

        private static char Flag(ModNeed n) => n == ModNeed.Required ? '1' : n == ModNeed.SameVersion ? '2' : '0';

        private static bool TryNeed(char c, out ModNeed need)
        {
            need = c == '1' ? ModNeed.Required : c == '2' ? ModNeed.SameVersion : ModNeed.OwnOnly;
            return c == '0' || c == '1' || c == '2';
        }

        private static string Clean(string s) => (s ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
    }

    /// <summary>What differs between my mods and another player's, counting only mods that are compared
    /// (<see cref="ModEntry.Compared"/> on either side).</summary>
    public sealed class ModDiff
    {
        /// <summary>The other player requires it and I do not have it.</summary>
        public List<ModEntry> Missing { get; } = new List<ModEntry>();
        /// <summary>I require it and the other player does not have it.</summary>
        public List<ModEntry> Extra { get; } = new List<ModEntry>();
        /// <summary>Both have it, one side compares it, and the version or the DLL differs.</summary>
        public List<(ModEntry Mine, ModEntry Theirs)> Different { get; } = new List<(ModEntry, ModEntry)>();

        public bool IsEmpty => Missing.Count == 0 && Extra.Count == 0 && Different.Count == 0;

        /// <summary>The question a joining player gets before it loads, in the game's popup; the table of differences
        /// goes under it.</summary>
        public const string PopupQuestion = "Your mods differ from the host's.\nJoin anyway?";

        /// <summary>The report, one line each; <paramref name="other"/> names the other player ("the host").</summary>
        public List<string> Lines(string other)
        {
            var lines = new List<string>();
            foreach (var m in Missing) lines.Add($"Missing: {m.Id} {m.Version} ({other} has it; everyone needs it)");
            foreach (var m in Extra) lines.Add($"Extra: {m.Id} {m.Version} (you have it, {other} does not; everyone needs it)");
            foreach (var (mine, theirs) in Different)
            {
                string why = (ModNeed)Math.Max((int)mine.Need, (int)theirs.Need) == ModNeed.Required
                    ? "everyone needs the same one" : "whoever has it needs the same one";
                lines.Add(mine.Version != theirs.Version
                    ? $"Different version: {mine.Id} {mine.Version}, {other} has {theirs.Version} ({why})"
                    : $"Different build: {mine.Id} {mine.Version} ({mine.Hash}), {other} has {theirs.Hash} ({why})");
            }
            return lines;
        }
    }

    public enum DiffKind
    {
        /// <summary>The host has it and the player does not.</summary>
        HostOnly,
        /// <summary>The player has it and the host does not.</summary>
        PlayerOnly,
        /// <summary>Both have it in another version.</summary>
        Version,
        /// <summary>Both have it in the same version, built differently (another DLL).</summary>
        Build,
    }

    /// <summary>
    /// The differences for the eye: one row per mod, the host's copy and the player's side by side, <see cref="Absent"/>
    /// where a side does not have it. The host's column comes first on both machines.
    /// </summary>
    public sealed class ModTable
    {
        public const string Absent = "\u2014";
        public const int BuildDigits = 6;

        public sealed class Row
        {
            public string Name { get; }
            public string Host { get; }
            public string Player { get; }
            public DiffKind Kind { get; }
            public Row(string name, string host, string player, DiffKind kind) { Name = name; Host = host; Player = player; Kind = kind; }
            public override string ToString() => $"{Name} | {Host} | {Player}";
        }

        public string HostHeader { get; }
        public string PlayerHeader { get; }
        public List<Row> Rows { get; } = new List<Row>();

        private ModTable(string hostHeader, string playerHeader) { HostHeader = hostHeader; PlayerHeader = playerHeader; }

        /// <summary>I am the joining player: "mine" is the player's side.</summary>
        public static ModTable ForPlayer(ModDiff d) => Fill(new ModTable("Host", "You"), d, iAmHost: false);

        /// <summary>I am the host: "mine" is the host's side; <paramref name="player"/> heads the other column.</summary>
        public static ModTable ForHost(ModDiff d, string player) => Fill(new ModTable("You (host)", player), d, iAmHost: true);

        private static ModTable Fill(ModTable t, ModDiff d, bool iAmHost)
        {
            void Add(string name, string mine, string theirs, DiffKind kind) =>
                t.Rows.Add(iAmHost ? new Row(name, mine, theirs, kind) : new Row(name, theirs, mine, kind));
            foreach (var m in d.Missing) Add(m.Id, Absent, m.Version, iAmHost ? DiffKind.PlayerOnly : DiffKind.HostOnly);
            foreach (var m in d.Extra) Add(m.Id, m.Version, Absent, iAmHost ? DiffKind.HostOnly : DiffKind.PlayerOnly);
            foreach (var (mine, theirs) in d.Different)
            {
                if (mine.Version != theirs.Version) Add(mine.Id, mine.Version, theirs.Version, DiffKind.Version);
                else Add(mine.Id, Built(mine), Built(theirs), DiffKind.Build);
            }
            return t;
        }

        private static string Built(ModEntry m) =>
            m.Version + " (" + (m.Hash.Length > BuildDigits ? m.Hash.Substring(0, BuildDigits) : m.Hash) + ")";

        /// <summary>How <see cref="RichText"/> lays the table out: where the columns start (percent of the text's
        /// width), how many characters a name and a cell keep, and the colours (any TextMeshPro colour; null: none).</summary>
        public sealed class RichStyle
        {
            public int HostAt = 52, PlayerAt = 76, NameChars = 24, CellChars = 15;
            public string Dim = "#9aa0a6", Absent = "#ff6b61", Cell = "#ffc766";
        }

        /// <summary>
        /// The table as TextMeshPro rich text for the game's popup: a dim header, then at most
        /// <paramref name="maxRows"/> lines of rows (when there are more, the last line counts the rest). Columns are
        /// set with <c>pos</c> tags; every line is aligned left.
        /// </summary>
        public string RichText(int maxRows, RichStyle style)
        {
            style ??= new RichStyle();
            var sb = new StringBuilder();
            sb.Append("<align=left>");
            sb.Append(Colored(style.Dim, Rich(Cut("Mod", style.NameChars)) + $"<pos={style.HostAt}%>" + Rich(Cut(HostHeader, style.CellChars))
                + $"<pos={style.PlayerAt}%>" + Rich(Cut(PlayerHeader, style.CellChars))));
            int shown = Math.Max(1, maxRows);
            if (Rows.Count > shown) shown = Math.Max(1, shown - 1); // the last line says how many more
            foreach (var r in Rows.Take(shown))
            {
                sb.Append('\n').Append(Rich(Cut(r.Name, style.NameChars)))
                  .Append($"<pos={style.HostAt}%>").Append(Cell(r.Host, style))
                  .Append($"<pos={style.PlayerAt}%>").Append(Cell(r.Player, style));
            }
            if (Rows.Count > shown) sb.Append('\n').Append(Colored(style.Dim, $"+{Rows.Count - shown} more (see the log)"));
            return sb.ToString();
        }

        private static string Cell(string text, RichStyle style) =>
            text == Absent ? Colored(style.Absent, Absent) : Colored(style.Cell, Rich(Cut(text, style.CellChars)));

        private static string Colored(string color, string text) => string.IsNullOrEmpty(color) ? text : $"<color={color}>{text}</color>";

        /// <summary>At most <paramref name="chars"/> characters, the cut marked with "..".</summary>
        public static string Cut(string text, int chars)
        {
            text ??= "";
            if (chars < 3 || text.Length <= chars) return text;
            return text.Substring(0, chars - 2).TrimEnd() + "..";
        }

        // A mod's name is the mod's own text: '<' would open a rich-text tag. A zero-width space after it breaks any tag.
        private static string Rich(string text) => (text ?? "").Replace("<", "<\u200B");
    }

    public static class ModCompare
    {
        public static ModDiff Compare(IEnumerable<ModEntry> mine, IEnumerable<ModEntry> theirs)
        {
            var diff = new ModDiff();
            var my = Index(mine);
            var their = Index(theirs);
            foreach (var kv in their)
                if (kv.Value.Required && !my.ContainsKey(kv.Key)) diff.Missing.Add(kv.Value);
            foreach (var kv in my)
            {
                if (!their.TryGetValue(kv.Key, out var t))
                {
                    if (kv.Value.Required) diff.Extra.Add(kv.Value);
                    continue;
                }
                var m = kv.Value;
                if (!m.Compared && !t.Compared) continue;
                bool versionDiffers = m.Version != t.Version;
                bool buildDiffers = m.Hash.Length > 0 && t.Hash.Length > 0 && !string.Equals(m.Hash, t.Hash, StringComparison.OrdinalIgnoreCase);
                if (versionDiffers || buildDiffers) diff.Different.Add((m, t));
            }
            diff.Missing.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            diff.Extra.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            diff.Different.Sort((a, b) => string.CompareOrdinal(a.Mine.Key, b.Mine.Key));
            return diff;
        }

        // Same key twice (two mods with one name): the first one is compared, as MelonLoader loads the first.
        internal static Dictionary<string, ModEntry> Index(IEnumerable<ModEntry> mods)
        {
            var d = new Dictionary<string, ModEntry>(StringComparer.Ordinal);
            foreach (var m in mods ?? Enumerable.Empty<ModEntry>())
                if (m != null && m.Key.Length > 0 && !d.ContainsKey(m.Key)) d[m.Key] = m;
            return d;
        }
    }

    // ====================================================================== the check before loading

    /// <summary>One mod as a short fingerprint of its name, version and DLL hash: two players' copies of a mod are the
    /// same exactly when the fingerprints are, so the joining player need not send its whole list. The tag is a
    /// shorter fingerprint of the name alone: "do you have this mod at all". Two names may share a tag; that only
    /// makes the host stop a player it need not stop, never let one in it should stop.</summary>
    public static class ModDigest
    {
        public const int Length = 8;
        public const int TagLength = 4;

        public static string Of(ModEntry m) => Hex(m.Key + "\t" + m.Version + "\t" + m.Hash.ToLowerInvariant(), Length);

        public static string TagOf(string key) => Hex("tag\t" + key, TagLength);

        public static bool IsHex(string d, int length) => d != null && d.Length == length && d.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));

        private static string Hex(string text, int length)
        {
            using var sha = SHA256.Create();
            var h = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
            return BitConverter.ToString(h, 0, length / 2).Replace("-", "").ToLowerInvariant();
        }
    }

    /// <summary>One of the joining player's mods as its offer carries it: no name, no version.</summary>
    public sealed class OfferedMod
    {
        public string Tag { get; }
        public string Digest { get; }
        public ModNeed Need { get; }
        public OfferedMod(string tag, string digest, ModNeed need) { Tag = tag; Digest = digest; Need = need; }

        public static OfferedMod Of(ModEntry m) => new OfferedMod(ModDigest.TagOf(m.Key), ModDigest.Of(m), m.Need);

        internal string Token => Tag + Digest + (Need == ModNeed.Required ? 'r' : Need == ModNeed.SameVersion ? 's' : 'o');

        internal static bool TryParse(string token, out OfferedMod mod)
        {
            mod = null;
            if (token == null || token.Length != ModDigest.TagLength + ModDigest.Length + 1) return false;
            string tag = token.Substring(0, ModDigest.TagLength), digest = token.Substring(ModDigest.TagLength, ModDigest.Length);
            char n = token[token.Length - 1];
            if (!ModDigest.IsHex(tag, ModDigest.TagLength) || !ModDigest.IsHex(digest, ModDigest.Length) || (n != 'r' && n != 's' && n != 'o')) return false;
            mod = new OfferedMod(tag, digest, n == 'r' ? ModNeed.Required : n == 's' ? ModNeed.SameVersion : ModNeed.OwnOnly);
            return true;
        }
    }

    /// <summary>
    /// What a joining player puts into its connection request, before it loads anything: for every mod it runs, the
    /// tag of its name, the fingerprint of its copy and its need (<see cref="OfferedMod"/>). The host decides from it
    /// whether the mods differ (<see cref="JoinCheck"/>). Wire: <c>POGMC2</c> and a mode letter, then after a 0x1E the
    /// mods, comma-separated, each <c>tag digest need</c> (4 + 8 hex digits, then r, s or o). None of it can hold ';'
    /// or '|', the game's separators.
    /// </summary>
    public sealed class JoinOffer
    {
        public const string Magic = "POGMC2";
        /// <summary>Every format of this mod starts so; an older offer in the data is replaced by the new one.</summary>
        public const string Family = "POGMC";
        private const char Field = '\u001e';

        public enum Kind
        {
            /// <summary>Stop me before I load if the mods differ, so I can choose.</summary>
            Ask,
            /// <summary>Let me in: I chose to join anyway, or I do not want to be stopped.</summary>
            Accept,
            /// <summary>My list was too long to send; let me in, the mods are compared after joining.</summary>
            Unchecked,
        }

        public Kind Mode { get; }
        public List<OfferedMod> Mods { get; }
        public HashSet<string> Digests { get; }
        private readonly Dictionary<string, List<OfferedMod>> _byTag;

        public JoinOffer(Kind mode, IEnumerable<OfferedMod> mods)
        {
            Mode = mode;
            Mods = (mods ?? Enumerable.Empty<OfferedMod>()).Where(m => m != null).OrderBy(m => m.Token, StringComparer.Ordinal).ToList();
            Digests = new HashSet<string>(Mods.Select(m => m.Digest), StringComparer.Ordinal);
            // A plain loop: the mod's build has no NullableAttribute for the compiler to put on a GroupBy lambda.
            _byTag = new Dictionary<string, List<OfferedMod>>(StringComparer.Ordinal);
            foreach (var m in Mods)
            {
                if (!_byTag.TryGetValue(m.Tag, out var list)) _byTag[m.Tag] = list = new List<OfferedMod>();
                list.Add(m);
            }
        }

        /// <summary>The player's mods whose name has this tag (usually one or none).</summary>
        public List<OfferedMod> WithTag(string tag) => _byTag.TryGetValue(tag, out var l) ? l : new List<OfferedMod>();

        /// <summary>The offer for my mods; a name twice counts once, as everywhere (<see cref="ModCompare"/>).</summary>
        public static JoinOffer From(IEnumerable<ModEntry> mods, Kind mode) =>
            new JoinOffer(mode, ModCompare.Index(mods).Values.Select(OfferedMod.Of));

        private static char Letter(Kind k) => k == Kind.Ask ? 'Q' : k == Kind.Accept ? 'A' : 'U';

        public string Serialize() => Magic + Letter(Mode) + Field + string.Join(",", Mods.Select(m => m.Token));

        /// <summary>The offer, or its <see cref="Kind.Unchecked"/> form when the list would be longer than
        /// <paramref name="maxChars"/> (the connection request has to fit in one network message).</summary>
        public string SerializeWithin(int maxChars)
        {
            string s = Serialize();
            return s.Length <= maxChars ? s : new JoinOffer(Kind.Unchecked, null).Serialize();
        }

        public static bool TryParse(string text, out JoinOffer offer)
        {
            offer = null;
            if (text == null || !text.StartsWith(Magic, StringComparison.Ordinal)) return false;
            var f = text.Split(Field);
            if (f.Length != 2 || f[0].Length != Magic.Length + 1) return false;
            Kind mode;
            switch (f[0][Magic.Length])
            {
                case 'Q': mode = Kind.Ask; break;
                case 'A': mode = Kind.Accept; break;
                case 'U': mode = Kind.Unchecked; break;
                default: return false;
            }
            var mods = new List<OfferedMod>();
            if (f[1].Length > 0)
                foreach (var token in f[1].Split(','))
                {
                    if (!OfferedMod.TryParse(token, out var m)) return false;
                    mods.Add(m);
                }
            offer = new JoinOffer(mode, mods);
            return true;
        }
    }

    /// <summary>
    /// The game's connection data (its NetworkContent): <c>POGC1|</c>, then its content keys, ';'-separated. The
    /// offer rides along as one more key: the game only asks whether its current content key is in the set, so a
    /// key it does not know changes nothing for it (a host without this mod lets the player in as before).
    /// </summary>
    public static class ConnectionPayload
    {
        public const string GamePrefix = "POGC1|";

        /// <summary>The data with <paramref name="offer"/> in it, an older offer dropped and the game's keys kept.
        /// Empty data gets the game's prefix (the game reads it the same as empty). Null when the data is in a form
        /// this version does not know: then it is left alone.</summary>
        public static string WithOffer(string text, string offer)
        {
            if (string.IsNullOrEmpty(text)) return GamePrefix + offer;
            if (!text.StartsWith(GamePrefix, StringComparison.Ordinal)) return null;
            var keys = text.Substring(GamePrefix.Length).Split(';')
                .Where(k => k.Length > 0 && !k.StartsWith(JoinOffer.Family, StringComparison.Ordinal)).ToList();
            keys.Add(offer);
            return GamePrefix + string.Join(";", keys);
        }

        /// <summary>The offer in the data, or null when there is none (the player has no POG Mod Check).</summary>
        public static string FindOffer(string text)
        {
            if (string.IsNullOrEmpty(text) || !text.StartsWith(GamePrefix, StringComparison.Ordinal)) return null;
            return text.Substring(GamePrefix.Length).Split(';').FirstOrDefault(k => k.StartsWith(JoinOffer.Magic, StringComparison.Ordinal));
        }
    }

    /// <summary>The host's side of the check before loading.</summary>
    public static class JoinCheck
    {
        /// <summary>
        /// Would <see cref="ModCompare.Compare"/> find a difference between my mods and the joining player's? A mod I
        /// require differs unless the player has the very same copy (name, version, DLL hash); a mod both have differs
        /// when either side compares it and the copies are not the same; a mod the player requires differs when I do
        /// not have it. An unknown DLL hash on one side, or two names sharing a tag, count as a difference here; the
        /// player's own exact comparison then lets it in without asking. Never misses what the comparison would report.
        /// </summary>
        public static bool Differs(IEnumerable<ModEntry> mine, JoinOffer offer)
        {
            var list = ModCompare.Index(mine).Values.ToList();
            foreach (var m in list)
            {
                if (offer.Digests.Contains(ModDigest.Of(m))) continue; // the player has this very copy
                if (m.Required) return true;
                var copies = offer.WithTag(ModDigest.TagOf(m.Key));
                if (copies.Count > 0 && (m.Compared || copies.Any(c => c.Need != ModNeed.OwnOnly))) return true;
            }
            var myTags = new HashSet<string>(list.Select(m => ModDigest.TagOf(m.Key)), StringComparer.Ordinal);
            return offer.Mods.Any(p => p.Need == ModNeed.Required && !myTags.Contains(p.Tag));
        }
    }

    /// <summary>
    /// The host's answer to a player whose mods differ, sent as the reason it is not let in: the host's mods that
    /// player needs for an exact comparison, as a <see cref="ModListCodec"/> list after <see cref="Prefix"/>. With it
    /// the player's <see cref="ModCompare.Compare"/> gives the same answer as with the host's whole list.
    /// </summary>
    public static class JoinRefusal
    {
        public const string Prefix = "POGModCheck:";

        /// <summary>My copies the comparison needs: every copy that differs where it matters (as in
        /// <see cref="JoinCheck.Differs"/>), and my copy of every mod the player requires, so a mod I have is not taken
        /// for one I lack.</summary>
        public static List<ModEntry> EntriesFor(IEnumerable<ModEntry> mine, JoinOffer offer)
        {
            var entries = new List<ModEntry>();
            foreach (var m in ModCompare.Index(mine).Values)
            {
                var copies = offer.WithTag(ModDigest.TagOf(m.Key));
                bool same = offer.Digests.Contains(ModDigest.Of(m));
                bool matters = m.Required || (copies.Count > 0 && (m.Compared || copies.Any(c => c.Need != ModNeed.OwnOnly)));
                if ((!same && matters) || copies.Any(c => c.Need == ModNeed.Required)) entries.Add(m);
            }
            entries.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            return entries;
        }

        /// <summary>The reason text; without the DLL hashes when it would be longer than <paramref name="maxChars"/>
        /// (then only names and versions are compared); null when even that is too long.</summary>
        public static string Build(IEnumerable<ModEntry> entries, int maxChars)
        {
            var list = (entries ?? Enumerable.Empty<ModEntry>()).ToList();
            string full = Prefix + ModListCodec.Serialize(list);
            if (full.Length <= maxChars) return full;
            string bare = Prefix + ModListCodec.Serialize(list.Select(m => new ModEntry(m.Id, m.Version, "", m.Need)));
            return bare.Length <= maxChars ? bare : null;
        }

        public static bool TryParse(string reason, out List<ModEntry> entries)
        {
            entries = null;
            if (reason == null || !reason.StartsWith(Prefix, StringComparison.Ordinal)) return false;
            return ModListCodec.TryParse(reason.Substring(Prefix.Length), out entries);
        }
    }

    /// <summary>
    /// A mod's own word on how much it matters to the other players, given with a standard .NET attribute, so the mod
    /// needs no reference to POG Mod Check and runs the same without it:
    /// <c>[assembly: System.Reflection.AssemblyMetadata("POGModCheck", "required")]</c> (every player needs it) or
    /// <c>"same-version"</c> (nobody has to have it; whoever has it needs the same version as the other side).
    /// </summary>
    public static class ModMarker
    {
        public const string Key = "POGModCheck";
        public const string RequiredValue = "required";
        public const string SameVersionValue = "same-version";

        public enum Mark
        {
            /// <summary>The mod says nothing: own only, unless something else makes it compared.</summary>
            None,
            Required,
            SameVersion,
            /// <summary>The key is there with a value this version does not know; treated as <see cref="None"/>.</summary>
            Unknown,
        }

        /// <summary>What a mod's metadata pairs say. The key ignores case and surrounding spaces; the value ignores case,
        /// spaces and punctuation ("Same Version" = "same-version"). "required" anywhere wins over "same-version",
        /// which wins over a value this version does not know (<see cref="Mark.Unknown"/>, given back in
        /// <paramref name="unknownValue"/>).</summary>
        public static Mark Interpret(IEnumerable<KeyValuePair<string, string>> metadata, out string unknownValue)
        {
            unknownValue = null;
            bool required = false, same = false, unknown = false;
            foreach (var kv in metadata ?? Enumerable.Empty<KeyValuePair<string, string>>())
            {
                if (!string.Equals(kv.Key?.Trim(), Key, StringComparison.OrdinalIgnoreCase)) continue;
                string value = ModEntry.KeyOf(kv.Value);
                if (value == ModEntry.KeyOf(RequiredValue)) required = true;
                else if (value == ModEntry.KeyOf(SameVersionValue)) same = true;
                else if (!unknown) { unknown = true; unknownValue = kv.Value?.Trim() ?? ""; }
            }
            if (required || same) { unknownValue = null; return required ? Mark.Required : Mark.SameVersion; }
            return unknown ? Mark.Unknown : Mark.None;
        }

        /// <summary>The mark an assembly carries. Reads the attribute data without building the attributes, and skips
        /// any attribute whose type does not load, so a mod with a missing dependency cannot break the reading.</summary>
        public static Mark Read(Assembly asm, out string unknownValue) => Interpret(MetadataOf(asm), out unknownValue);

        private static List<KeyValuePair<string, string>> MetadataOf(Assembly asm)
        {
            var pairs = new List<KeyValuePair<string, string>>();
            if (asm == null) return pairs;
            IList<CustomAttributeData> attributes;
            try { attributes = asm.GetCustomAttributesData(); }
            catch { return pairs; }
            string metadataType = typeof(AssemblyMetadataAttribute).FullName;
            foreach (var a in attributes)
            {
                try
                {
                    if (a.AttributeType.FullName != metadataType) continue;
                    var args = a.ConstructorArguments;
                    if (args.Count == 2) pairs.Add(new KeyValuePair<string, string>(args[0].Value as string, args[1].Value as string));
                }
                catch { /* an attribute whose type does not load: not this one */ }
            }
            return pairs;
        }
    }

    /// <summary>
    /// The host's view of who has sent a mod list. A player that joined and sent none within the wait has no POG Mod
    /// Check (or one too old to speak this format); each such player is named once. Times are seconds on any clock
    /// that only goes forward.
    /// </summary>
    public sealed class PeerWatch
    {
        private readonly ulong _self;
        private readonly float _wait;
        private readonly Dictionary<ulong, float> _joinedAt = new Dictionary<ulong, float>();
        private readonly HashSet<ulong> _answered = new HashSet<ulong>();
        private readonly HashSet<ulong> _named = new HashSet<ulong>();

        /// <param name="self">The host's own id, never watched.</param>
        /// <param name="wait">Seconds a joined player has to send its list.</param>
        public PeerWatch(ulong self, float wait) { _self = self; _wait = wait; }

        /// <summary>The players connected now. A new one starts its wait; one that left is no longer waited for.</summary>
        public void Present(IEnumerable<ulong> ids, float now)
        {
            var present = new HashSet<ulong>(ids ?? Enumerable.Empty<ulong>());
            present.Remove(_self);
            foreach (var id in present)
                if (!_joinedAt.ContainsKey(id)) _joinedAt[id] = now;
            foreach (var id in _joinedAt.Keys.Where(k => !present.Contains(k)).ToList()) _joinedAt.Remove(id);
        }

        /// <summary>A list came from this player (it may come before the player shows in <see cref="Present"/>).</summary>
        public void Answered(ulong id) => _answered.Add(id);

        /// <summary>Players still connected that waited longer than the wait with no list; each one only once.</summary>
        public List<ulong> Overdue(float now)
        {
            var due = new List<ulong>();
            foreach (var kv in _joinedAt)
                if (!_answered.Contains(kv.Key) && !_named.Contains(kv.Key) && now - kv.Value > _wait) due.Add(kv.Key);
            foreach (var id in due) _named.Add(id);
            due.Sort();
            return due;
        }
    }
}
