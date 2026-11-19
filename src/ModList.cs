using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace POGMods.ModCheck
{
    /// <summary>One mod as one player has it.</summary>
    public sealed class ModEntry
    {
        /// <summary>The mod's name as MelonLoader shows it (MelonInfo).</summary>
        public string Id { get; }
        public string Version { get; }
        /// <summary>First 12 hex digits of the SHA-256 of the mod's DLL ("" when unknown).</summary>
        public string Hash { get; }
        /// <summary>Every player needs it: it adds network content (through POGContentLib) or the config says so.
        /// Otherwise "own only": what one player runs is nobody else's business.</summary>
        public bool Required { get; }

        public ModEntry(string id, string version, string hash, bool required)
        {
            Id = id ?? ""; Version = version ?? ""; Hash = hash ?? ""; Required = required;
        }

        /// <summary>Ids match ignoring case, spaces and punctuation ("POG Custom Items" = "POGCustomItems").</summary>
        public string Key => KeyOf(Id);

        public static string KeyOf(string id)
        {
            if (string.IsNullOrEmpty(id)) return "";
            var sb = new StringBuilder(id.Length);
            foreach (char c in id) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        public override string ToString() => $"{Id} {Version}" + (Hash.Length > 0 ? $" ({Hash})" : "") + (Required ? " [required]" : "");
    }

    /// <summary>
    /// The mod list on the wire: <c>POGMC1</c>, then one line per mod, <c>id \t version \t hash \t 0|1</c>.
    /// Tabs and line breaks inside a field are replaced by spaces. Format "1": a change gets a new magic.
    /// </summary>
    public static class ModListCodec
    {
        public const string Magic = "POGMC1";

        public static string Serialize(IEnumerable<ModEntry> mods)
        {
            var sb = new StringBuilder(Magic);
            foreach (var m in mods ?? Enumerable.Empty<ModEntry>())
                sb.Append('\n').Append(Clean(m.Id)).Append('\t').Append(Clean(m.Version)).Append('\t')
                  .Append(Clean(m.Hash)).Append('\t').Append(m.Required ? '1' : '0');
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
                if (f.Length != 4 || f[0].Length == 0 || (f[3] != "0" && f[3] != "1")) return false;
                mods.Add(new ModEntry(f[0], f[1], f[2], f[3] == "1"));
            }
            return true;
        }

        private static string Clean(string s) => (s ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
    }

    /// <summary>What differs between my mods and another player's, counting only mods someone requires.</summary>
    public sealed class ModDiff
    {
        /// <summary>The other player requires it and I do not have it.</summary>
        public List<ModEntry> Missing { get; } = new List<ModEntry>();
        /// <summary>I require it and the other player does not have it.</summary>
        public List<ModEntry> Extra { get; } = new List<ModEntry>();
        /// <summary>Both have it, someone requires it, and the version or the DLL differs.</summary>
        public List<(ModEntry Mine, ModEntry Theirs)> Different { get; } = new List<(ModEntry, ModEntry)>();

        public bool IsEmpty => Missing.Count == 0 && Extra.Count == 0 && Different.Count == 0;

        /// <summary>The report, one line each; <paramref name="other"/> names the other player ("the host").</summary>
        public List<string> Lines(string other)
        {
            var lines = new List<string>();
            foreach (var m in Missing) lines.Add($"Missing: {m.Id} {m.Version} ({other} has it; everyone needs it)");
            foreach (var m in Extra) lines.Add($"Extra: {m.Id} {m.Version} (you have it, {other} does not; everyone needs it)");
            foreach (var (mine, theirs) in Different)
                lines.Add(mine.Version != theirs.Version
                    ? $"Different version: {mine.Id} {mine.Version}, {other} has {theirs.Version}"
                    : $"Different build: {mine.Id} {mine.Version} ({mine.Hash}), {other} has {theirs.Hash}");
            return lines;
        }
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
                if (!m.Required && !t.Required) continue;
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
        private static Dictionary<string, ModEntry> Index(IEnumerable<ModEntry> mods)
        {
            var d = new Dictionary<string, ModEntry>(StringComparer.Ordinal);
            foreach (var m in mods ?? Enumerable.Empty<ModEntry>())
                if (m != null && m.Key.Length > 0 && !d.ContainsKey(m.Key)) d[m.Key] = m;
            return d;
        }
    }

    /// <summary>
    /// A mod's own word that every player needs it, given with a standard .NET attribute, so the mod needs no
    /// reference to POG Mod Check and runs the same without it:
    /// <c>[assembly: System.Reflection.AssemblyMetadata("POGModCheck", "required")]</c>.
    /// </summary>
    public static class ModMarker
    {
        public const string Key = "POGModCheck";
        public const string RequiredValue = "required";

        public enum Mark
        {
            /// <summary>The mod says nothing: own only, unless something else makes it required.</summary>
            None,
            Required,
            /// <summary>The key is there with a value this version does not know; treated as <see cref="None"/>.</summary>
            Unknown,
        }

        /// <summary>What a mod's metadata pairs say. Key and value ignore case and surrounding spaces; "required"
        /// anywhere wins; another value under the key is <see cref="Mark.Unknown"/>, given back in
        /// <paramref name="unknownValue"/>.</summary>
        public static Mark Interpret(IEnumerable<KeyValuePair<string, string>> metadata, out string unknownValue)
        {
            unknownValue = null;
            var mark = Mark.None;
            foreach (var kv in metadata ?? Enumerable.Empty<KeyValuePair<string, string>>())
            {
                if (!string.Equals(kv.Key?.Trim(), Key, StringComparison.OrdinalIgnoreCase)) continue;
                string value = kv.Value?.Trim() ?? "";
                if (string.Equals(value, RequiredValue, StringComparison.OrdinalIgnoreCase)) { unknownValue = null; return Mark.Required; }
                mark = Mark.Unknown;
                unknownValue ??= value;
            }
            return mark;
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
