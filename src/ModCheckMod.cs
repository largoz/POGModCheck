using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using MelonLoader;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

[assembly: MelonInfo(typeof(POGMods.ModCheck.ModCheckMod), "POG Mod Check", POGMods.ModCheck.ModCheckMod.VERSION, "largo", "https://github.com/largoz/POGModCheck")]
[assembly: MelonGame(null, "Pit of Goblin")]

namespace POGMods.ModCheck
{
    /// <summary>
    /// After a player joins, the host and that player swap their mod lists (name, version, DLL hash) over an
    /// NGO named message. A mod is "required for everyone" when it adds network content through POGContentLib
    /// (<c>Content.ContentAssemblies</c>), when its author marks it (<see cref="ModMarker"/>), or when the config lists
    /// it; any other mod is "own only" and never reported. Differences open a window (missing, extra, different
    /// version or build) that does not stop the game and closes with F10 or its button; every report also goes to the
    /// log. A side that hears nothing from the other within <see cref="NoAnswerAfter"/> says so: the other side has no
    /// POG Mod Check.
    /// </summary>
    public sealed class ModCheckMod : MelonMod
    {
        public const string VERSION = "0.1.0";
        private const string MessageName = "POGModCheck/List";
        private const float NoAnswerAfter = 15f;
        private const float WatchEvery = 1f;

        private MelonPreferences_Entry<string> _requiredCfg;
        private MelonPreferences_Entry<bool> _showWindow;

        private List<ModEntry> _local;
        private string _localText;

        // Session state, reset when the session ends.
        private CustomMessagingManager _registeredOn;
        private CustomMessagingManager.HandleNamedMessageDelegate _handler;
        private bool _sent;
        private float _sentAt;
        private bool _answered, _noAnswerReported;
        private PeerWatch _peers;          // host: who has sent a list
        private float _nextWatch;
        private bool _cannotListPlayers;   // said once per game

        private readonly ModCheckWindow _window = new ModCheckWindow();

        public override void OnInitializeMelon()
        {
            var cat = MelonPreferences.CreateCategory("POGModCheck");
            _requiredCfg = cat.CreateEntry("Required", "",
                description: "Mods every player must have, besides those found by themselves (network content through POGContentLib, or marked by their authors). Names as in the MelonLoader console, comma-separated.");
            _showWindow = cat.CreateEntry("ShowWindow", true, description: "Show the window when mods differ (the log always gets the report).");
        }

        public override void OnUpdate()
        {
            if (Input.GetKeyDown(KeyCode.F10)) _window.Close();
            try { Tick(); }
            catch (Exception ex) { LoggerInstance.Warning($"Mod check: {ex.GetType().Name}: {ex.Message}"); ResetSession(); }
        }

        public override void OnGUI() => _window.Draw();

        // ------------------------------------------------------------------ session

        private void Tick()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsListening) { if (_registeredOn != null || _sent || _peers != null) ResetSession(); return; }
            var cmm = nm.CustomMessagingManager;
            if (cmm == null) return;

            if (_registeredOn == null || _registeredOn.Pointer != cmm.Pointer)
            {
                if (_handler == null) { Action<ulong, FastBufferReader> h = OnMessage; _handler = h; }
                cmm.RegisterNamedMessageHandler(MessageName, _handler);
                _registeredOn = cmm;
                EnsureLocal();
            }

            if (nm.IsServer) { WatchPlayers(nm); return; }

            if (nm.IsConnectedClient && !_sent)
            {
                Send(cmm, NetworkManager.ServerClientId);
                _sent = true; _sentAt = Time.unscaledTime;
                LoggerInstance.Msg($"Sent my mod list to the host ({_local.Count} mods, {_local.Count(m => m.Required)} required).");
            }

            if (_sent && !_answered && !_noAnswerReported && Time.unscaledTime - _sentAt > NoAnswerAfter)
            {
                _noAnswerReported = true;
                LoggerInstance.Msg("The host did not answer the mod check: it has no POG Mod Check (or it is too old). Nothing to compare.");
                ShowSilence("Mod check", "The host did not answer the mod check (no POG Mod Check there?).");
            }
        }

        /// <summary>Host: a joined player that sends no list in time has no POG Mod Check; say so once per player.</summary>
        private void WatchPlayers(NetworkManager nm)
        {
            float now = Time.unscaledTime;
            if (now < _nextWatch) return;
            _nextWatch = now + WatchEvery;
            var ids = ConnectedIds(nm);
            if (ids == null) return;
            Peers().Present(ids, now);
            foreach (var id in Peers().Overdue(now))
            {
                LoggerInstance.Msg($"Player {id} sent no mod list in {NoAnswerAfter:0} s: it has no POG Mod Check (or it is too old), so its mods cannot be compared.");
                ShowSilence($"Player {id}: no mod check", $"Player {id} sent no mod list (no POG Mod Check there?).");
            }
        }

        // The interop shows ConnectedClientsIds as an interface without Count; the object behind it is a List
        // (as POGContentLib's shop tools read it).
        private List<ulong> ConnectedIds(NetworkManager nm)
        {
            var list = nm.ConnectedClientsIds?.TryCast<Il2CppSystem.Collections.Generic.List<ulong>>();
            if (list == null)
            {
                if (!_cannotListPlayers)
                {
                    _cannotListPlayers = true;
                    LoggerInstance.Warning("Cannot read the list of joined players: a player without POG Mod Check will not be reported.");
                }
                return null;
            }
            var ids = new List<ulong>(list.Count);
            for (int i = 0; i < list.Count; i++) ids.Add(list[i]);
            return ids;
        }

        private PeerWatch Peers() => _peers ??= new PeerWatch(NetworkManager.ServerClientId, NoAnswerAfter);

        /// <summary>The other side is silent. The window opens only when I run mods everyone needs: otherwise
        /// nothing of mine can be missing over there.</summary>
        private void ShowSilence(string title, string first)
        {
            var required = _local?.Where(m => m.Required).Select(m => m.Id).ToList() ?? new List<string>();
            if (required.Count == 0 || !_showWindow.Value) return;
            _window.Show(title, new List<string> { first, "You run mods every player needs: " + string.Join(", ", required) + "." });
        }

        private void ResetSession()
        {
            _registeredOn = null; _sent = false; _answered = false; _noAnswerReported = false;
            _peers = null; _nextWatch = 0f;
        }

        private void OnMessage(ulong sender, FastBufferReader reader)
        {
            try
            {
                var nm = NetworkManager.Singleton;
                bool server = nm != null && nm.IsServer;
                if (server) Peers().Answered(sender);   // even an unreadable list means the mod is there
                string text = ReadText(reader);
                if (!ModListCodec.TryParse(text, out var theirs))
                {
                    LoggerInstance.Warning($"Unreadable mod list from {(sender == NetworkManager.ServerClientId ? "the host" : $"player {sender}")}.");
                    return;
                }
                EnsureLocal();
                if (server)
                {
                    // A player joined: answer with the host's list, then report what differs.
                    Send(nm.CustomMessagingManager, sender);
                    Report($"player {sender}", theirs, $"Player {sender} joined with different mods");
                }
                else if (sender == NetworkManager.ServerClientId)
                {
                    _answered = true;
                    Report("the host", theirs, "Your mods differ from the host's");
                }
            }
            catch (Exception ex) { LoggerInstance.Warning($"Mod list from {sender}: {ex.GetType().Name}: {ex.Message}"); }
        }

        private void Report(string other, List<ModEntry> theirs, string title)
        {
            var diff = ModCompare.Compare(_local, theirs);
            if (diff.IsEmpty)
            {
                LoggerInstance.Msg($"Mods match {other} ({theirs.Count} mods there, {theirs.Count(m => m.Required)} required).");
                return;
            }
            var lines = diff.Lines(other);
            LoggerInstance.Msg($"{title}:");
            foreach (var l in lines) LoggerInstance.Msg("  " + l);
            if (_showWindow.Value) _window.Show(title, lines);
        }

        // ------------------------------------------------------------------ wire

        private void Send(CustomMessagingManager cmm, ulong to)
        {
            if (cmm == null) return;
            var bytes = Encoding.UTF8.GetBytes(_localText);
            var w = new FastBufferWriter(bytes.Length + 4, Allocator.Temp);
            try
            {
                for (int i = 0; i < 4; i++) w.WriteByteSafe((byte)(bytes.Length >> (8 * i)));
                foreach (var b in bytes) w.WriteByteSafe(b);
                cmm.SendNamedMessage(MessageName, to, w, NetworkDelivery.ReliableFragmentedSequenced);
            }
            finally { w.Dispose(); }
        }

        private static string ReadText(FastBufferReader r)
        {
            int length = 0;
            for (int i = 0; i < 4; i++) { r.ReadByteSafe(out byte b); length |= b << (8 * i); }
            if (length < 0 || length > 64 * 1024 || length > r.Length - r.Position) return null;
            var bytes = new byte[length];
            for (int i = 0; i < length; i++) { r.ReadByteSafe(out byte b); bytes[i] = b; }
            return Encoding.UTF8.GetString(bytes);
        }

        // ------------------------------------------------------------------ my mods

        private void EnsureLocal()
        {
            if (_local != null) return;
            var contentAssemblies = ContentAssemblies();
            var configured = new HashSet<string>((_requiredCfg.Value ?? "").Split(',').Select(ModEntry.KeyOf).Where(k => k.Length > 0));
            _local = new List<ModEntry>();
            var why = new List<string>();
            foreach (MelonBase melon in MelonMod.RegisteredMelons.Cast<MelonBase>().Concat(MelonPlugin.RegisteredMelons))
            {
                var asm = melon.MelonAssembly?.Assembly;
                string asmName = asm?.GetName().Name ?? "";
                var mark = ModMarker.Read(asm, out string unknownMark);
                if (mark == ModMarker.Mark.Unknown)
                    LoggerInstance.Warning($"{melon.Info.Name} marks itself \"{ModMarker.Key}\" = \"{unknownMark}\"; only \"{ModMarker.RequiredValue}\" is known here, so the mark is ignored.");
                string reason =
                    contentAssemblies.Contains(asmName) ? "network content through POGContentLib"
                    : mark == ModMarker.Mark.Required ? "its author marks it"
                    : configured.Contains(ModEntry.KeyOf(melon.Info.Name)) || configured.Contains(ModEntry.KeyOf(asmName)) ? "your config"
                    : null;
                if (reason != null) why.Add($"{melon.Info.Name} ({reason})");
                _local.Add(new ModEntry(melon.Info.Name, melon.Info.Version, HashOf(asm), reason != null));
            }
            _localText = ModListCodec.Serialize(_local);
            LoggerInstance.Msg($"My mods: {string.Join("; ", _local)}");
            LoggerInstance.Msg(why.Count > 0 ? $"Every player needs: {string.Join("; ", why)}." : "None of my mods is one every player needs.");
        }

        /// <summary>
        /// Assemblies that registered network content through POGContentLib. Read by reflection, so the mod runs
        /// without the library; an older library without the list answers through its content ids.
        /// </summary>
        private HashSet<string> ContentAssemblies()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var lib = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "POGContentLib");
                var content = lib?.GetType("POGContentLib.Items.Content");
                if (content == null) return set;
                if (content.GetProperty("ContentAssemblies", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is IEnumerable<string> names)
                {
                    foreach (var n in names) set.Add(n);
                    return set;
                }
                // Older library: the content ids' mod part, matched to mods by name.
                var parity = content.GetProperty("Parity", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                var manifest = parity?.GetType().GetProperty("LocalManifest")?.GetValue(parity);
                if (manifest?.GetType().GetProperty("Entries")?.GetValue(manifest) is System.Collections.IEnumerable entries)
                {
                    var modKeys = new HashSet<string>();
                    foreach (var e in entries)
                    {
                        var full = e?.GetType().GetProperty("FullId")?.GetValue(e) as string;
                        if (!string.IsNullOrEmpty(full)) modKeys.Add(ModEntry.KeyOf(full.Split(':')[0]));
                    }
                    if (modKeys.Count > 0) set.Add("POGContentLib");
                    foreach (MelonBase melon in MelonMod.RegisteredMelons)
                    {
                        string asmName = melon.MelonAssembly?.Assembly?.GetName().Name ?? "";
                        if (modKeys.Contains(ModEntry.KeyOf(asmName)) || modKeys.Contains(ModEntry.KeyOf(melon.Info.Name))) set.Add(asmName);
                    }
                }
            }
            catch (Exception ex) { LoggerInstance.Warning($"Reading POGContentLib's content list: {ex.Message}"); }
            return set;
        }

        private static string HashOf(Assembly asm)
        {
            try
            {
                string path = asm?.Location;
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "";
                using var sha = SHA256.Create();
                var h = sha.ComputeHash(File.ReadAllBytes(path));
                return BitConverter.ToString(h, 0, 6).Replace("-", "").ToLowerInvariant();
            }
            catch { return ""; }
        }
    }

    /// <summary>The report window: top right, never takes the mouse or the keys; F10 or its button closes it.
    /// Reports stack (newest last, a few at most) so a second player joining does not hide the first's report.</summary>
    internal sealed class ModCheckWindow
    {
        private const int MaxReports = 4;
        private readonly List<(string Title, List<string> Lines)> _reports = new List<(string, List<string>)>();
        private GUIStyle _text, _head;

        /// <summary>Adds a report; one with the same title replaces the old one.</summary>
        public void Show(string title, List<string> lines)
        {
            _reports.RemoveAll(r => r.Title == title);
            _reports.Add((title, lines ?? new List<string>()));
            while (_reports.Count > MaxReports) _reports.RemoveAt(0);
        }

        public void Close() => _reports.Clear();

        public void Draw()
        {
            if (_reports.Count == 0) return;
            if (_text == null)
            {
                _text = new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = 15 };
                _head = new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold };
                _head.normal.textColor = new Color(1f, 0.82f, 0.35f);
            }
            float w = Mathf.Min(560f, Screen.width - 32f), x = Screen.width - w - 16f, y = 16f;
            float inner = w - 24f;
            float h = 8f;
            foreach (var (_, lines) in _reports)
            {
                h += 30f;
                foreach (var l in lines) h += _text.CalcHeight(new GUIContent(l), inner) + 2f;
                h += 6f;
            }
            h += 40f;
            GUI.Box(new Rect(x, y, w, h), "");
            GUI.Box(new Rect(x, y, w, h), "");
            float cy = y + 6f;
            foreach (var (title, lines) in _reports)
            {
                GUI.Label(new Rect(x + 12f, cy, inner, 26f), title, _head);
                cy += 30f;
                foreach (var l in lines)
                {
                    float lh = _text.CalcHeight(new GUIContent(l), inner);
                    GUI.Label(new Rect(x + 12f, cy, inner, lh), l, _text);
                    cy += lh + 2f;
                }
                cy += 6f;
            }
            if (GUI.Button(new Rect(x + w - 132f, cy + 4f, 120f, 28f), "Close (F10)")) Close();
            GUI.Label(new Rect(x + 12f, cy + 8f, w - 160f, 24f), "The game goes on; the log has the same list.", _text);
        }
    }
}
