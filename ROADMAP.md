# POG Mod Check: roadmap

State on 2026-11-19: 0.1.0, not on Thunderstore. Shown over LAN (PC host, laptop client, run 15 in
`/run/media/tim/Games/pog-runs/lan-1/run15`): a client without a required mod, a client without POG Mod Check, and a
client with everything; F10 closes the window. The full record is P7 in POGContentLib's `ROADMAP.md`.

## Next

- [ ] **Check before loading, with a choice** (the user's word, 2026-11-19): as on a modded Minecraft server, the
      differences should show before the player loads into the host's game, with **Join** and **Cancel**. Today the
      lists are swapped after the connection, so the client is already in the hub when the report comes (run 15:
      in the hub at 13:34:33.8, report at 13:34:34.6). Two ways, to pick after reading how the game joins (decompile):
      1. **The lobby**: the host puts its required mods into the lobby's data; the client reads them before it
         connects and asks Join / Cancel. Closest to Minecraft; depends on the game's lobby (EOS) and does not reach
         a LAN join that bypasses the lobby (POGLanTest).
      2. **NGO connection approval**: the client sends its list with the connection request; the host refuses with
         a reason that carries its own list; the client shows the choice and, on Join, connects again marked "I
         accept". Works on any transport, LAN included; the second connection has to go through the game's own join.
      The in-game window stays for the host (who joined with what) and for players who join anyway.
- [ ] **D13 The game's toasts cover the window** (run 15, laptop: an "Added to …" toast in the top right hid it
      until the toast left). The game's canvas draws over IMGUI. Move the window out of the toasts' corner.
- [ ] **D14 F10 is also POGSpawner's key**: closing the report also toggles the spawner panel when both are
      installed. Make the key a setting.
- [ ] Not run yet: a host without POG Mod Check (the client's "host did not answer" path; unit logic only).
- [ ] Shop files without a DLL (they add slots, so they are network content) have no melon to list.

## Publishing

Waits for the user's word. The icon is `icon.png` (1024x1024, made by the user); the publish script
(`_Modding/tools/ThunderStoreCLI.py`) packs it at 256x256 by itself.
