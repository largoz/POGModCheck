using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppTMPro;
using MelonLoader;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;
using GameNetworkHandler = Il2CppGame.Networking.NetworkHandler;
using GameNetworkContent = Il2CppGame.Networking.NetworkContent;
using GameMenu = Il2CppGame.Menu.NetworkMenu;
using GamePopup = Il2CppGame.Menu.NetworkMenuPopup;

[assembly: MelonInfo(typeof(POGMods.ModCheck.ModCheckMod), "POG Mod Check", POGMods.ModCheck.ModCheckMod.VERSION, "largo", "https://github.com/largoz/POGModCheck")]
[assembly: MelonGame(null, "Pit of Goblin")]
[assembly: HarmonyDontPatchAll]
// The LAN test mod (never shipped) shows the popup and the window with made-up differences to check their layout.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("POGLanTest")]

namespace POGMods.ModCheck
{
    /// <summary>
    /// Two checks. <b>Before loading</b>: a joining player puts a fingerprint of its mods into the game's connection
    /// request (<see cref="JoinOffer"/>); a host whose required mods differ does not let it in, and gives its own list
    /// as the reason (<see cref="JoinRefusal"/>). The player then sees the game's own popup with the differences and
    /// chooses Join anyway or Cancel. The game's own "missing dungeon content" retry is the model: the player stays in
    /// the join while it chooses, and Join anyway connects again saying it accepts. <b>After joining</b>: the host and
    /// the player swap their whole mod lists over an NGO named message; differences open a window that does not stop
    /// the game (F10 or its button closes it), and a side that hears nothing within <see cref="NoAnswerAfter"/> says
    /// the other has no POG Mod Check. A mod is "required for everyone" when it adds network content through
    /// POGLib (<c>Content.ContentAssemblies</c>), when its author marks it (<see cref="ModMarker"/>), or when
    /// the config lists it; "same version" (nobody has to have it, whoever has it needs the same copy) when its
    /// author or the config says so; any other mod is "own only" and never reported. Every report also goes to the
    /// log.
    /// </summary>
    public sealed class ModCheckMod : MelonMod
    {
        public const string VERSION = "0.1.1";
        private const string MessageName = "POGModCheck/List";
        private const float NoAnswerAfter = 15f;
        private const float WatchEvery = 1f;
        /// <summary>The content key handed to the game so it keeps the join going (no "lost connection" popup, the
        /// player stays in the lobby) while the player chooses; the game's retry for it is taken over here.</summary>
        internal const string RetrySentinel = "POGModCheck:choice";
        private const int MaxOfferChars = 600;    // the connection request has to fit one network message
        private const int MaxRefusalChars = 450;  // the reason travels as 2-byte characters in one network message
        private const float PopupLostAfter = 1f;  // our popup closed without an answer: treat as Cancel

        internal static ModCheckMod Instance;

        private MelonPreferences_Entry<string> _requiredCfg;
        private MelonPreferences_Entry<string> _sameVersionCfg;
        private MelonPreferences_Entry<bool> _showWindow;
        private MelonPreferences_Entry<bool> _askBeforeJoin;

        // The check before loading. Hooks that did not apply (the game changed) turn it off on that side.
        private bool _hostHook, _playerHooks;
        private JoinOffer.Kind _offerSent = JoinOffer.Kind.Unchecked; // what the latest connection request carries
        private bool _accepted;       // the next requests say Accept: the player chose to join anyway
        private bool _joinedAnyway;   // the differences were shown before joining: no in-game window for them again
        private ModDiff _choice;      // a refusal waiting for the player's answer
        private bool _choiceShown, _choiceInGamePopup;
        private float _popupGoneSince = -1f;
        private int _answer;          // 0 none, 1 join anyway, 2 cancel (set by the popup, acted on in OnUpdate)
        private bool _joinWhenShutDown;
        private bool _passThrough;    // our own call into the game's lost-connection path
        private int _idleFrames;
        private bool _unknownDataWarned;
        private readonly ChoiceWindow _fallback = new ChoiceWindow();
        private readonly ChoicePopup _popup = new ChoicePopup();

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
                description: "Mods every player must have, besides those found by themselves (network content through POGLib, or marked by their authors). Names as in the MelonLoader console, comma-separated.");
            _sameVersionCfg = cat.CreateEntry("SameVersion", "",
                description: "Mods nobody has to have, but whoever has them must run the same version as the other side (besides those marked so by their authors). Names as in the MelonLoader console, comma-separated.");
            _showWindow = cat.CreateEntry("ShowWindow", true, description: "Show the window when mods differ (the log always gets the report).");
            _askBeforeJoin = cat.CreateEntry("AskBeforeJoin", true,
                description: "Check mods before a player loads into a game. As the host: a joining player whose mods differ is stopped and chooses Join anyway or Cancel (that player needs POG Mod Check too). As a player: when off, you are never stopped; the report shows after joining instead.");
            Instance = this;
            Hook();
        }

        private void Hook()
        {
            _hostHook = TryPatch(typeof(Patch_Approve));
            bool start = TryPatch(typeof(Patch_StartClient));
            bool lost = TryPatch(typeof(Patch_LostConnection));
            bool step = TryPatch(typeof(Patch_ReconnectStep));
            bool disconnected = TryPatch(typeof(Patch_ClientDisconnected));
            TryPatch(typeof(Patch_UpdateConnectionData)); // keeps the offer after the game rewrites its data; optional
            _playerHooks = start && lost && step && disconnected;
            if (!_hostHook) LoggerInstance.Warning("As the host this game cannot stop a joining player whose mods differ (the game changed); the report shows after joining.");
            if (!_playerHooks) LoggerInstance.Warning("As a player this game cannot ask before loading (the game changed); the report shows after joining.");
        }

        private bool TryPatch(Type patch)
        {
            try { HarmonyInstance.CreateClassProcessor(patch).Patch(); return true; }
            catch (Exception ex) { LoggerInstance.Warning($"Hook {patch.Name} not applied: {ex.GetBaseException().Message}"); return false; }
        }

        public override void OnUpdate()
        {
            if (Input.GetKeyDown(KeyCode.F10)) _window.Close();
            try { TickJoin(); }
            catch (Exception ex) { LoggerInstance.Warning($"Mod check before loading: {ex.GetType().Name}: {ex.Message}"); }
            try { Tick(); }
            catch (Exception ex) { LoggerInstance.Warning($"Mod check: {ex.GetType().Name}: {ex.Message}"); ResetSession(); }
        }

        public override void OnGUI()
        {
            _window.Draw();
            int a = _fallback.Draw();
            if (a != 0 && _answer == 0) _answer = a;
        }

        // ------------------------------------------------------------------ before loading: the host

        /// <summary>The game's connection approval, before the game's own check: a player whose offer asks to be
        /// stopped and whose required mods differ is not let in; the reason carries what it needs to compare.</summary>
        internal bool OnApproving(NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse response)
        {
            if (request == null || response == null || !_askBeforeJoin.Value) return true;
            ulong id = request.ClientNetworkId;
            if (id == NetworkManager.ServerClientId) return true;
            string offerText = ConnectionPayload.FindOffer(TextOf(request.Payload));
            if (offerText == null) return true; // no POG Mod Check there: the watch after joining says so
            if (!JoinOffer.TryParse(offerText, out var offer))
            {
                LoggerInstance.Warning($"Player {id} sent a mod check this version cannot read; letting it in, the mods are compared after joining.");
                return true;
            }
            if (offer.Mode == JoinOffer.Kind.Accept) { LoggerInstance.Msg($"Player {id} joins without being stopped (it chose Join anyway, or it does not want the check before loading)."); return true; }
            if (offer.Mode == JoinOffer.Kind.Unchecked) { LoggerInstance.Msg($"Player {id} has too many mods to check before loading; they are compared after joining."); return true; }
            EnsureLocal();
            if (!JoinCheck.Differs(_local, offer)) return true;
            string reason = JoinRefusal.Build(JoinRefusal.EntriesFor(_local, offer), MaxRefusalChars);
            if (reason == null)
            {
                LoggerInstance.Msg($"Player {id}'s mods differ, but my list is too long to send before loading; letting it in, the report shows after joining.");
                return true;
            }
            LoggerInstance.Msg($"Player {id} asks to join with different mods: not let in yet; it sees the differences and chooses Join anyway or Cancel.");
            response.Approved = false;
            response.Reason = reason;
            response.Pending = false;
            return false;
        }

        // ------------------------------------------------------------------ before loading: the player

        internal void BeforeStartClient(NetworkManager nm)
        {
            if (_choice != null || _joinWhenShutDown) DropChoice("a new join started");
            Stamp(nm);
            LoggerInstance.Msg(_offerSent == JoinOffer.Kind.Ask
                ? $"Joining: the host may stop me before loading if required mods differ ({_local.Count} mods, {_local.Count(m => m.Required)} required)."
                : $"Joining: the host will not stop me ({_offerSent}).");
        }

        /// <summary>Puts my offer into the game's connection data, keeping the game's own keys.</summary>
        internal void Stamp(NetworkManager nm)
        {
            var cfg = nm?.NetworkConfig;
            if (cfg == null) return;
            EnsureLocal();
            var kind = _accepted || !_askBeforeJoin.Value || !_playerHooks ? JoinOffer.Kind.Accept : JoinOffer.Kind.Ask;
            string offer = JoinOffer.From(_local, kind).SerializeWithin(MaxOfferChars);
            string text = TextOf(cfg.ConnectionData);
            string next = ConnectionPayload.WithOffer(text, offer);
            if (next == null)
            {
                if (!_unknownDataWarned)
                {
                    _unknownDataWarned = true;
                    LoggerInstance.Warning("The game's connection data has a form this version does not know, so the mods are compared after joining, not before.");
                }
                _offerSent = JoinOffer.Kind.Unchecked;
                return;
            }
            _offerSent = JoinOffer.TryParse(offer, out var sent) ? sent.Mode : JoinOffer.Kind.Unchecked;
            if (next != text) cfg.ConnectionData = Encoding.UTF8.GetBytes(next);
        }

        /// <summary>The game lost the connection. A refusal from a host with POG Mod Check is turned into the game's
        /// own "content retry", so the game keeps the join going instead of going back to the menu; the retry itself
        /// is taken over by <see cref="OnRetryTakenOver"/>.</summary>
        internal void OnLostConnection(GameNetworkHandler h, bool host)
        {
            if (host || _passThrough || h == null || _offerSent != JoinOffer.Kind.Ask) return;
            if (!string.IsNullOrEmpty(h.m_contentRetryKey)) return; // ours already (OnClientDisconnected), or the game's
            var nm = h.m_networkManager ?? NetworkManager.Singleton;
            if (!JoinRefusal.TryParse(nm?.DisconnectReason, out var hostMods)) return;
            TakeRefusal(h, hostMods);
        }

        /// <summary>
        /// The game reads the reason first in OnClientDisconnected (before OnLostConnection): for its own
        /// "POG_CONTENT:" it sets the retry key and shows nothing, for any other reason it shows "Disconnected" with
        /// that text and keeps it for the main menu. A refusal from POG Mod Check is handled the way the game handles
        /// its own: the key is set, the reason cleared, nothing shown. True: handled, the game's method is skipped
        /// (for the local player it does only this, after a log line and clearing what OnLostConnection clears too).
        /// </summary>
        internal bool OnClientDisconnected(GameNetworkHandler h, ulong clientId)
        {
            if (_passThrough || h == null || _offerSent != JoinOffer.Kind.Ask) return false;
            var nm = h.m_networkManager ?? NetworkManager.Singleton;
            if (nm == null || nm.IsServer || clientId != nm.LocalClientId) return false;
            if (!string.IsNullOrEmpty(h.m_contentRetryKey)) return false;
            if (!JoinRefusal.TryParse(nm.DisconnectReason, out var hostMods)) return false;
            TakeRefusal(h, hostMods);
            return true;
        }

        private void TakeRefusal(GameNetworkHandler h, List<ModEntry> hostMods)
        {
            h.m_disconnectReason = "";   // or the main menu shows "Disconnected" with it later
            EnsureLocal();
            _choice = ModCompare.Compare(_local, hostMods);
            _choiceShown = false; _answer = 0; _popupGoneSince = -1f;
            h.m_contentRetryKey = RetrySentinel;
            var lines = _choice.Lines("the host");
            LoggerInstance.Msg(lines.Count == 0
                ? "The host stopped me before loading, but my exact comparison finds nothing that matters (a DLL hash is unknown on one side): joining."
                : "The host stopped me before loading; my mods differ from the host's:");
            foreach (var l in lines) LoggerInstance.Msg("  " + l);
        }

        /// <summary>The game started its retry for our key: it is ours; the join stays open while the player chooses.</summary>
        internal void OnRetryTakenOver(GameNetworkHandler h)
        {
            if (h != null) h.m_contentReconnecting = true; // as the game's retry does, so the join is not reported failed
            if (_choice != null && _choice.IsEmpty) { _choice = null; _answer = 1; }
        }

        private void TickJoin()
        {
            var nm = NetworkManager.Singleton;
            if (_choice != null && !_choiceShown) ShowChoice();
            if (_choice != null && _choiceShown && _choiceInGamePopup && _answer == 0) WatchPopup();

            if (_answer != 0)
            {
                int a = _answer; _answer = 0;
                CloseChoice();
                if (a == 1)
                {
                    LoggerInstance.Msg("Join anyway: connecting again, telling the host I accept the differences.");
                    _accepted = true; _joinedAnyway = true; _joinWhenShutDown = true;
                }
                else ReturnToMenu("Cancelled: back to the menu, nothing was loaded.");
            }

            if (_joinWhenShutDown && nm != null && !nm.ShutdownInProgress && !nm.IsListening)
            {
                _joinWhenShutDown = false;
                JoinAgain(nm);
            }

            // An acceptance lasts for one join, through the game's own retries, until the player is out again.
            if ((_accepted || _joinedAnyway) && _choice == null && !_joinWhenShutDown)
            {
                var h = GameNetworkHandler.Singleton;
                bool busy = (nm != null && nm.IsListening) || (h != null && h.m_contentReconnecting);
                _idleFrames = busy ? 0 : _idleFrames + 1;
                if (_idleFrames > 2) { _accepted = false; _joinedAnyway = false; _idleFrames = 0; }
            }
        }

        private void ShowChoice()
        {
            _choiceShown = true;
            var table = ModTable.ForPlayer(_choice);
            try
            {
                Action<GamePopup.PopupButtonFlag> cb = OnPopupButton;
                if (_popup.Show(ModDiff.PopupQuestion, table, cb, w => LoggerInstance.Warning(w)))
                {
                    _choiceInGamePopup = true;
                    LoggerInstance.Msg("Asking the player: Join anyway or Cancel (the game's popup).");
                    return;
                }
            }
            catch (Exception ex) { LoggerInstance.Warning($"The game's popup failed ({ex.Message}); using my own window."); _popup.Close(); }
            _choiceInGamePopup = false;
            _fallback.Show(ModDiff.PopupQuestion, table);
            LoggerInstance.Msg("Asking the player: Join anyway or Cancel (my own window; Enter joins, Esc cancels).");
        }

        private void OnPopupButton(GamePopup.PopupButtonFlag button)
        {
            if (_answer == 0) _answer = button == GamePopup.PopupButtonFlag.Custom ? 1 : 2;
        }

        // Something else closed the game's popup without an answer, or put its own message in it (another popup, a
        // state change): do not leave the join hanging; it counts as Cancel. A popup that took ours over gets its
        // layout back at once.
        private void WatchPopup()
        {
            if (_popup.StillOurs) { _popupGoneSince = -1f; return; }
            if (_popup.Replaced) _popup.Close();
            if (_popupGoneSince < 0f) { _popupGoneSince = Time.unscaledTime; return; }
            if (Time.unscaledTime - _popupGoneSince > PopupLostAfter)
            {
                LoggerInstance.Msg("The popup closed without an answer: taking it as Cancel.");
                _answer = 2;
            }
        }

        private void CloseChoice()
        {
            _choice = null; _choiceShown = false; _popupGoneSince = -1f;
            _popup.Close();
            _fallback.Close();
        }

        /// <summary>A choice nobody will answer any more (another join started): close it, end our retry.</summary>
        private void DropChoice(string why)
        {
            if (_choiceInGamePopup && _choiceShown) _popup.Hide();
            CloseChoice(); _answer = 0; _joinWhenShutDown = false;
            var h = GameNetworkHandler.Singleton;
            if (h != null) h.m_contentReconnecting = false;
            LoggerInstance.Msg($"The open question was dropped: {why}.");
        }

        /// <summary>Join anyway: as the game's retry does after loading the content: the prefabs again, then the client.</summary>
        private void JoinAgain(NetworkManager nm)
        {
            var h = GameNetworkHandler.Singleton;
            if (h == null) { ReturnToMenu("Cannot join again: the game's network handler is gone."); return; }
            h.m_contentReconnecting = false;
            h.m_contentRetried = false; // the game may still need its own content retry on this join
            h.EnsureNetworkPrefabsRegistered();
            if (!nm.StartClient()) ReturnToMenu("The game did not start the client again: back to the menu.");
        }

        /// <summary>Cancel: the game's own path for a lost connection (back to the menu, out of the lobby), without
        /// its "lost connection" popup: the player chose this.</summary>
        private void ReturnToMenu(string why)
        {
            _accepted = false; _joinedAnyway = false; _joinWhenShutDown = false;
            LoggerInstance.Msg(why);
            var h = GameNetworkHandler.Singleton;
            if (h == null) return;
            h.m_contentReconnecting = false;
            h.m_disconnectReason = ""; // the main menu would show it as "Disconnected"
            var eos = h.m_onlineService;
            Il2CppSystem.Action<string, string> saved = null;
            if (eos != null) { saved = eos.OnConnectionFailed; eos.OnConnectionFailed = null; }
            _passThrough = true;
            try
            {
                h.m_contentRetryKey = null;
                h.m_contentRetried = true; // the game must not read the refusal again
                h.OnLostConnection(false);
            }
            finally
            {
                _passThrough = false;
                if (eos != null) eos.OnConnectionFailed = saved;
            }
            try { h.m_callback?.Invoke(false); } catch (Exception ex) { LoggerInstance.Warning($"The join's callback: {ex.Message}"); }
        }

        private static string TextOf(Il2CppStructArray<byte> data)
        {
            if (data == null) return "";
            var b = new byte[data.Length];
            for (int i = 0; i < b.Length; i++) b[i] = data[i];
            return Encoding.UTF8.GetString(b);
        }

        // ------------------------------------------------------------------ after joining: the session

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
        // (as POGLib's shop tools read it).
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
                    Report($"player {sender}", theirs, $"Player {sender} joined with different mods", d => ModTable.ForHost(d, $"Player {sender}"));
                }
                else if (sender == NetworkManager.ServerClientId)
                {
                    _answered = true;
                    // Shown before loading and accepted: the log gets it again, the window does not.
                    Report("the host", theirs, "Your mods differ from the host's", ModTable.ForPlayer, window: !_joinedAnyway);
                }
            }
            catch (Exception ex) { LoggerInstance.Warning($"Mod list from {sender}: {ex.GetType().Name}: {ex.Message}"); }
        }

        private void Report(string other, List<ModEntry> theirs, string title, Func<ModDiff, ModTable> table, bool window = true)
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
            if (_showWindow.Value && window) _window.Show(title, table(diff));
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
            var required = Names(_requiredCfg.Value);
            var same = Names(_sameVersionCfg.Value);
            _local = new List<ModEntry>();
            var everyone = new List<string>();
            var sameVersion = new List<string>();
            foreach (MelonBase melon in MelonMod.RegisteredMelons.Cast<MelonBase>().Concat(MelonPlugin.RegisteredMelons))
            {
                var asm = melon.MelonAssembly?.Assembly;
                string asmName = asm?.GetName().Name ?? "";
                var mark = ModMarker.Read(asm, out string unknownMark);
                if (mark == ModMarker.Mark.Unknown)
                    LoggerInstance.Warning($"{melon.Info.Name} marks itself \"{ModMarker.Key}\" = \"{unknownMark}\"; only \"{ModMarker.RequiredValue}\" and \"{ModMarker.SameVersionValue}\" are known here, so the mark is ignored.");
                bool Listed(HashSet<string> names) => names.Contains(ModEntry.KeyOf(melon.Info.Name)) || names.Contains(ModEntry.KeyOf(asmName));
                string reason;
                var need = ModNeed.Required;
                if (contentAssemblies.Contains(asmName)) reason = "network content through POGLib";
                else if (mark == ModMarker.Mark.Required) reason = "its author marks it";
                else if (Listed(required)) reason = "your config";
                else
                {
                    need = ModNeed.SameVersion;
                    reason = mark == ModMarker.Mark.SameVersion ? "its author marks it" : Listed(same) ? "your config" : null;
                    if (reason == null) need = ModNeed.OwnOnly;
                }
                if (need == ModNeed.Required) everyone.Add($"{melon.Info.Name} ({reason})");
                else if (need == ModNeed.SameVersion) sameVersion.Add($"{melon.Info.Name} ({reason})");
                _local.Add(new ModEntry(melon.Info.Name, melon.Info.Version, HashOf(asm), need));
            }
            _localText = ModListCodec.Serialize(_local);
            LoggerInstance.Msg($"My mods: {string.Join("; ", _local)}");
            LoggerInstance.Msg(everyone.Count > 0 ? $"Every player needs: {string.Join("; ", everyone)}." : "None of my mods is one every player needs.");
            if (sameVersion.Count > 0) LoggerInstance.Msg($"Whoever has them needs the same version: {string.Join("; ", sameVersion)}.");
        }

        private static HashSet<string> Names(string csv) =>
            new HashSet<string>((csv ?? "").Split(',').Select(ModEntry.KeyOf).Where(k => k.Length > 0));

        /// <summary>
        /// Assemblies that registered network content through POGLib. Read by reflection, so the mod runs
        /// without the library; an older library without the list answers through its content ids.
        /// </summary>
        private HashSet<string> ContentAssemblies()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var lib = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "POGLib");
                var content = lib?.GetType("POGLib.Items.Content");
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
                    if (modKeys.Count > 0) set.Add("POGLib");
                    foreach (MelonBase melon in MelonMod.RegisteredMelons)
                    {
                        string asmName = melon.MelonAssembly?.Assembly?.GetName().Name ?? "";
                        if (modKeys.Contains(ModEntry.KeyOf(asmName)) || modKeys.Contains(ModEntry.KeyOf(melon.Info.Name))) set.Add(asmName);
                    }
                }
            }
            catch (Exception ex) { LoggerInstance.Warning($"Reading POGLib's content list: {ex.Message}"); }
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

    /// <summary>A <see cref="ModTable"/> drawn with IMGUI: the mod's name, then the host's column and the player's;
    /// a side without the mod shows a red dash, two copies that differ show in amber.</summary>
    internal sealed class TableGui
    {
        public const float RowHeight = 22f;
        public const int MaxRows = 10;
        private GUIStyle _cell, _dim, _absent, _changed;

        public static int LinesOf(ModTable t) => 1 + Math.Min(t.Rows.Count, MaxRows) + (t.Rows.Count > MaxRows ? 1 : 0);

        public float Height(ModTable t) => LinesOf(t) * RowHeight;

        /// <summary>Draws the table at (x, y), <paramref name="w"/> wide; returns the y under it.</summary>
        public float Draw(ModTable t, float x, float y, float w)
        {
            if (_cell == null)
            {
                _cell = new GUIStyle(GUI.skin.label) { fontSize = 15, wordWrap = false, clipping = TextClipping.Clip };
                _dim = new GUIStyle(_cell); _dim.normal.textColor = new Color(0.65f, 0.65f, 0.65f);
                _absent = new GUIStyle(_cell) { fontStyle = FontStyle.Bold }; _absent.normal.textColor = new Color(1f, 0.42f, 0.38f);
                _changed = new GUIStyle(_cell); _changed.normal.textColor = new Color(1f, 0.78f, 0.4f);
            }
            float nameW = w * 0.48f, colW = w * 0.25f, hostX = x + w * 0.50f, playerX = x + w * 0.75f;
            GUI.Label(new Rect(x, y, nameW, RowHeight), "Mod", _dim);
            GUI.Label(new Rect(hostX, y, colW, RowHeight), t.HostHeader, _dim);
            GUI.Label(new Rect(playerX, y, colW, RowHeight), t.PlayerHeader, _dim);
            y += RowHeight;
            int shown = t.Rows.Count > MaxRows ? MaxRows : t.Rows.Count;
            foreach (var r in t.Rows.Take(shown))
            {
                GUI.Label(new Rect(x, y, nameW, RowHeight), r.Name, _cell);
                GUI.Label(new Rect(hostX, y, colW, RowHeight), r.Host, StyleOf(r.Host));
                GUI.Label(new Rect(playerX, y, colW, RowHeight), r.Player, StyleOf(r.Player));
                y += RowHeight;
            }
            if (t.Rows.Count > shown)
            {
                GUI.Label(new Rect(x, y, w, RowHeight), $"+{t.Rows.Count - shown} more (see the log)", _dim);
                y += RowHeight;
            }
            return y;
        }

        private GUIStyle StyleOf(string cell) => cell == ModTable.Absent ? _absent : _changed;
    }

    /// <summary>The report window: top right, never takes the mouse or the keys; F10 or its button closes it.
    /// Reports stack (newest last, a few at most) so a second player joining does not hide the first's report.</summary>
    internal sealed class ModCheckWindow
    {
        private const int MaxReports = 4;
        private sealed class Report { public string Title; public List<string> Lines; public ModTable Table; }
        private readonly List<Report> _reports = new List<Report>();
        private readonly TableGui _table = new TableGui();
        private GUIStyle _text, _head;

        /// <summary>Adds a report; one with the same title replaces the old one.</summary>
        public void Show(string title, List<string> lines) => Add(new Report { Title = title, Lines = lines ?? new List<string>() });

        public void Show(string title, ModTable table) => Add(new Report { Title = title, Lines = new List<string>(), Table = table });

        private void Add(Report r)
        {
            _reports.RemoveAll(x => x.Title == r.Title);
            _reports.Add(r);
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
            foreach (var r in _reports)
            {
                h += 30f;
                foreach (var l in r.Lines) h += _text.CalcHeight(new GUIContent(l), inner) + 2f;
                if (r.Table != null) h += _table.Height(r.Table);
                h += 6f;
            }
            h += 40f;
            GUI.Box(new Rect(x, y, w, h), "");
            GUI.Box(new Rect(x, y, w, h), "");
            float cy = y + 6f;
            foreach (var r in _reports)
            {
                GUI.Label(new Rect(x + 12f, cy, inner, 26f), r.Title, _head);
                cy += 30f;
                foreach (var l in r.Lines)
                {
                    float lh = _text.CalcHeight(new GUIContent(l), inner);
                    GUI.Label(new Rect(x + 12f, cy, inner, lh), l, _text);
                    cy += lh + 2f;
                }
                if (r.Table != null) cy = _table.Draw(r.Table, x + 12f, cy, inner);
                cy += 6f;
            }
            if (GUI.Button(new Rect(x + w - 132f, cy + 4f, 120f, 28f), "Close (F10)")) Close();
            GUI.Label(new Rect(x + 12f, cy + 8f, w - 160f, 24f), "The game goes on; the log has the details.", _text);
        }
    }

    /// <summary>
    /// The game's menu popup made to hold the table. The question and the buttons stay the game's; the table, in a text
    /// object of its own, takes the place of the popup's small error line, and the window grows to fit it. All of it
    /// is put back when the question closes, so the game's next popup looks as before. Sizes are the canvas's units
    /// (the game's window is 400 x 150 of them).
    /// </summary>
    internal sealed class ChoicePopup
    {
        private const float Width = 470f;
        private const float Padding = 20f;      // the window's layout pads 10 on each side
        private const float FontSize = 12.5f;
        private const string TableFont = "Roboto-Medium SDF"; // the game's small-label font: plain digits
        private const string ObjectName = "POGModCheck Table";
        private const int Rows = 6;
        private static readonly ModTable.RichStyle Style = new ModTable.RichStyle();

        private GamePopup _shownOn;
        private string _question;
        private RectTransform _window;
        private Vector2 _windowSize;
        private GameObject _table, _error;
        private bool _errorWasActive;
        private static TMP_FontAsset _font;

        /// <summary>Our question is the one the popup shows.</summary>
        public bool StillOurs
        {
            get { try { return _shownOn != null && _shownOn.IsShowing && _shownOn.m_messageText.text == _question; } catch { return false; } }
        }

        /// <summary>The popup shows, with another message: the game put its own in.</summary>
        public bool Replaced
        {
            get { try { return _shownOn != null && _shownOn.IsShowing && _shownOn.m_messageText.text != _question; } catch { return false; } }
        }

        /// <summary>Shows the question with the table; false when the game's popup is not there. When the table does
        /// not fit (the game changed), the popup still asks, with a one-line summary in its error line.</summary>
        public bool Show(string question, ModTable table, Action<GamePopup.PopupButtonFlag> answer, Action<string> warn)
        {
            Close();
            GamePopup popup = null;
            try { popup = GameMenu.Instance?.Popup; } catch { }
            if (popup == null) return false;
            // The custom button's label is centred over its key glyph: a short word keeps clear of it.
            popup.ShowPopup(question, "Join", GamePopup.PopupButtonFlag.Custom | GamePopup.PopupButtonFlag.Cancel, answer, false, false, "");
            _shownOn = popup; _question = question;
            try { Fit(popup, table); }
            catch (Exception ex)
            {
                warn($"The table did not fit the game's popup ({ex.GetType().Name}: {ex.Message}); a short line instead.");
                Restore();
                try { popup.m_errorText.text = string.Join("; ", table.Rows.Take(2).Select(r => $"{r.Name}: host {r.Host}, you {r.Player}")); } catch { }
            }
            return true;
        }

        private void Fit(GamePopup popup, ModTable table)
        {
            var error = popup.m_errorText;
            _window = popup.m_window.GetComponent<RectTransform>();
            _windowSize = _window.sizeDelta;
            float errorLine = error.GetComponent<LayoutElement>()?.preferredHeight ?? 32f;

            var go = new GameObject(ObjectName);
            go.transform.SetParent(error.transform.parent, false);
            go.transform.SetSiblingIndex(error.transform.GetSiblingIndex() + 1);
            _table = go;
            var text = go.AddComponent<TextMeshProUGUI>();
            text.font = FontOr(error.font);
            text.fontSize = FontSize;
            text.fontStyle = FontStyles.Normal;
            text.enableWordWrapping = false;
            text.overflowMode = TextOverflowModes.Overflow;
            text.richText = true;
            text.alignment = TextAlignmentOptions.TopLeft;
            text.color = Color.white;
            text.raycastTarget = false;
            string rich = table.RichText(Rows, Style);
            text.text = rich;
            float width = Math.Max(_windowSize.x, Width);
            float height = text.GetPreferredValues(rich, width - Padding, 0f).y + 6f;
            var layout = go.AddComponent<LayoutElement>();
            layout.preferredHeight = height; layout.minHeight = height;

            _error = error.gameObject; _errorWasActive = _error.activeSelf;
            _error.SetActive(false);
            _window.sizeDelta = new Vector2(width, _windowSize.y - errorLine + height);
        }

        private static TMP_FontAsset FontOr(TMP_FontAsset fallback)
        {
            if (_font == null)
                try { _font = Resources.FindObjectsOfTypeAll<TMP_FontAsset>().FirstOrDefault(f => f != null && f.name == TableFont); } catch { }
            return _font ?? fallback;
        }

        /// <summary>The game's layout as it was; the popup itself is left as it is.</summary>
        private void Restore()
        {
            try { if (_table != null) UnityEngine.Object.Destroy(_table); } catch { }
            try { if (_error != null) _error.SetActive(_errorWasActive); } catch { }
            try { if (_window != null) _window.sizeDelta = _windowSize; } catch { }
            _table = null; _error = null; _window = null;
        }

        /// <summary>Our question is over: the layout goes back.</summary>
        public void Close()
        {
            Restore();
            _shownOn = null; _question = null;
        }

        /// <summary>Closes the popup if it still shows our question, then puts the layout back.</summary>
        public void Hide()
        {
            try { if (StillOurs) _shownOn.HidePopup(); } catch { }
            Close();
        }
    }

    /// <summary>The question before loading when the game's popup is not there: centred, Enter joins, Esc cancels.</summary>
    internal sealed class ChoiceWindow
    {
        private string _question;
        private ModTable _tableData;
        private readonly TableGui _table = new TableGui();
        private GUIStyle _style;

        public void Show(string question, ModTable table) { _question = question; _tableData = table; }
        public void Close() { _question = null; _tableData = null; }

        /// <summary>Draws the window; 1 = Join anyway, 2 = Cancel, 0 = no answer yet.</summary>
        public int Draw()
        {
            if (_question == null) return 0;
            var e = Event.current;
            if (e != null && e.type == EventType.KeyDown)
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) { Close(); return 1; }
                if (e.keyCode == KeyCode.Escape) { Close(); return 2; }
            }
            _style ??= new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = 16 };
            float w = Mathf.Min(620f, Screen.width - 32f), inner = w - 32f;
            float qh = _style.CalcHeight(new GUIContent(_question), inner);
            float th = _tableData != null ? _table.Height(_tableData) + 8f : 0f;
            float h = qh + th + 76f, x = (Screen.width - w) / 2f, y = (Screen.height - h) / 2f;
            GUI.Box(new Rect(x, y, w, h), "POG Mod Check");
            GUI.Box(new Rect(x, y, w, h), "");
            GUI.Label(new Rect(x + 16f, y + 24f, inner, qh), _question, _style);
            if (_tableData != null) _table.Draw(_tableData, x + 16f, y + 24f + qh + 8f, inner);
            if (GUI.Button(new Rect(x + w - 292f, y + h - 40f, 132f, 28f), "Join anyway")) { Close(); return 1; }
            if (GUI.Button(new Rect(x + w - 148f, y + h - 40f, 132f, 28f), "Cancel")) { Close(); return 2; }
            return 0;
        }
    }

    // ---------------------------------------------------------------------- hooks (applied one by one in Hook())

    [HarmonyPatch(typeof(GameNetworkHandler), nameof(GameNetworkHandler.OnApprovingConnection))]
    internal static class Patch_Approve
    {
        static bool Prefix(NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse response)
        {
            try { return ModCheckMod.Instance?.OnApproving(request, response) ?? true; }
            catch (Exception ex) { MelonLogger.Warning($"[POG Mod Check] Approving a join: {ex.GetType().Name}: {ex.Message}; letting it in."); return true; }
        }
    }

    [HarmonyPatch(typeof(NetworkManager), nameof(NetworkManager.StartClient))]
    internal static class Patch_StartClient
    {
        static void Prefix(NetworkManager __instance)
        {
            try { ModCheckMod.Instance?.BeforeStartClient(__instance); }
            catch (Exception ex) { MelonLogger.Warning($"[POG Mod Check] Before joining: {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    [HarmonyPatch(typeof(GameNetworkContent), nameof(GameNetworkContent.UpdateConnectionData))]
    internal static class Patch_UpdateConnectionData
    {
        static void Postfix()
        {
            try { ModCheckMod.Instance?.Stamp(NetworkManager.Singleton); }
            catch (Exception ex) { MelonLogger.Warning($"[POG Mod Check] Connection data: {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    [HarmonyPatch(typeof(GameNetworkHandler), nameof(GameNetworkHandler.OnLostConnection))]
    internal static class Patch_LostConnection
    {
        static void Prefix(GameNetworkHandler __instance, bool host)
        {
            try { ModCheckMod.Instance?.OnLostConnection(__instance, host); }
            catch (Exception ex) { MelonLogger.Warning($"[POG Mod Check] Lost connection: {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    [HarmonyPatch(typeof(GameNetworkHandler), nameof(GameNetworkHandler.OnClientDisconnected))]
    internal static class Patch_ClientDisconnected
    {
        static bool Prefix(GameNetworkHandler __instance, ulong clientID)
        {
            try { return !(ModCheckMod.Instance?.OnClientDisconnected(__instance, clientID) ?? false); }
            catch (Exception ex) { MelonLogger.Warning($"[POG Mod Check] Client disconnected: {ex.GetType().Name}: {ex.Message}"); return true; }
        }
    }

    // IL2CPP inlined NetworkHandler.ReconnectWithContent into OnLostConnection, so its coroutine's step is hooked.
    [HarmonyPatch(typeof(GameNetworkHandler._ReconnectWithContent_d__116), nameof(GameNetworkHandler._ReconnectWithContent_d__116.MoveNext))]
    internal static class Patch_ReconnectStep
    {
        static bool Prefix(GameNetworkHandler._ReconnectWithContent_d__116 __instance, ref bool __result)
        {
            try
            {
                if (__instance.contentKey != ModCheckMod.RetrySentinel) return true;
                __instance.__1__state = -1; // finished: the game's retry does nothing for our key
                __result = false;
                ModCheckMod.Instance?.OnRetryTakenOver(__instance.__4__this);
                return false;
            }
            catch (Exception ex) { MelonLogger.Warning($"[POG Mod Check] Taking over the retry: {ex.GetType().Name}: {ex.Message}"); return true; }
        }
    }
}
