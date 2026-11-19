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
    /// (<c>Content.ContentAssemblies</c>) or the config lists it; any other mod is "own only" and never reported.
    /// Differences open a window (missing, extra, different version or build) that does not stop the game and
    /// closes with F10 or its button; every report also goes to the log.
    /// </summary>
    public sealed class ModCheckMod : MelonMod
    {
        public const string VERSION = "0.1.0";
        private const string MessageName = "POGModCheck/List";
        private const float NoAnswerAfter = 15f;

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

        private readonly ModCheckWindow _window = new ModCheckWindow();

        public override void OnInitializeMelon()
        {
            var cat = MelonPreferences.CreateCategory("POGModCheck");
            _requiredCfg = cat.CreateEntry("Required", "",
                description: "Mods every player must have, besides those that add network content through POGContentLib (found by themselves). Names as in the MelonLoader console, comma-separated.");
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
            if (nm == null || !nm.IsListening) { if (_registeredOn != null || _sent) ResetSession(); return; }
            var cmm = nm.CustomMessagingManager;
            if (cmm == null) return;

            if (_registeredOn == null || _registeredOn.Pointer != cmm.Pointer)
            {
                if (_handler == null) { Action<ulong, FastBufferReader> h = OnMessage; _handler = h; }
                cmm.RegisterNamedMessageHandler(MessageName, _handler);
                _registeredOn = cmm;
                EnsureLocal();
            }

            if (!nm.IsServer && nm.IsConnectedClient && !_sent)
            {
                Send(cmm, NetworkManager.ServerClientId);
                _sent = true; _sentAt = Time.unscaledTime;
                LoggerInstance.Msg($"Sent my mod list to the host ({_local.Count} mods, {_local.Count(m => m.Required)} required).");
            }

            if (_sent && !_answered && !_noAnswerReported && Time.unscaledTime - _sentAt > NoAnswerAfter)
            {
                _noAnswerReported = true;
                var required = _local.Where(m => m.Required).ToList();
                LoggerInstance.Msg("The host did not answer the mod check: it has no POG Mod Check (or it is too old). Nothing to compare.");
                if (required.Count > 0 && _showWindow.Value)
                    _window.Show("Mod check", new List<string> { "The host did not answer the mod check (no POG Mod Check there?).",
                        "You run mods every player needs: " + string.Join(", ", required.Select(m => m.Id)) + "." });
            }
        }

        private void ResetSession()
        {
            _registeredOn = null; _sent = false; _answered = false; _noAnswerReported = false;
        }

        private void OnMessage(ulong sender, FastBufferReader reader)
        {
            try
            {
                string text = ReadText(reader);
                if (!ModListCodec.TryParse(text, out var theirs))
                {
                    LoggerInstance.Warning($"Unreadable mod list from {(sender == NetworkManager.ServerClientId ? "the host" : $"player {sender}")}.");
                    return;
                }
                var nm = NetworkManager.Singleton;
                EnsureLocal();
                if (nm != null && nm.IsServer)
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
            foreach (MelonBase melon in MelonMod.RegisteredMelons.Cast<MelonBase>().Concat(MelonPlugin.RegisteredMelons))
            {
                var asm = melon.MelonAssembly?.Assembly;
                string asmName = asm?.GetName().Name ?? "";
                bool required = contentAssemblies.Contains(asmName)
                                || configured.Contains(ModEntry.KeyOf(melon.Info.Name)) || configured.Contains(ModEntry.KeyOf(asmName));
                _local.Add(new ModEntry(melon.Info.Name, melon.Info.Version, HashOf(asm), required));
            }
            _localText = ModListCodec.Serialize(_local);
            LoggerInstance.Msg($"My mods: {string.Join("; ", _local)}");
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

    /// <summary>The report window: top right, never takes the mouse or the keys; F10 or its button closes it.</summary>
    internal sealed class ModCheckWindow
    {
        private string _title;
        private List<string> _lines;
        private GUIStyle _text, _head;

        public void Show(string title, List<string> lines) { _title = title; _lines = lines; }
        public void Close() { _lines = null; }

        public void Draw()
        {
            if (_lines == null) return;
            if (_text == null)
            {
                _text = new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = 15 };
                _head = new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold };
                _head.normal.textColor = new Color(1f, 0.82f, 0.35f);
            }
            float w = Mathf.Min(560f, Screen.width - 32f), x = Screen.width - w - 16f, y = 16f;
            float lineH = 0f;
            foreach (var l in _lines) lineH += _text.CalcHeight(new GUIContent(l), w - 24f) + 2f;
            float h = 34f + lineH + 44f;
            GUI.Box(new Rect(x, y, w, h), "");
            GUI.Box(new Rect(x, y, w, h), "");
            GUI.Label(new Rect(x + 12f, y + 6f, w - 24f, 26f), _title, _head);
            float cy = y + 36f;
            foreach (var l in _lines)
            {
                float lh = _text.CalcHeight(new GUIContent(l), w - 24f);
                GUI.Label(new Rect(x + 12f, cy, w - 24f, lh), l, _text);
                cy += lh + 2f;
            }
            if (GUI.Button(new Rect(x + w - 132f, cy + 6f, 120f, 28f), "Close (F10)")) Close();
            GUI.Label(new Rect(x + 12f, cy + 10f, w - 160f, 24f), "The game goes on; the log has the same list.", _text);
        }
    }
}
