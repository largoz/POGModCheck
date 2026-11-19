# POG Mod Check: roadmap

State on 2026-11-19: 0.1.1, on Thunderstore. Run 16 added the check before loading, run 19 the table and the "same version" kind (below). Shown over LAN (PC host, laptop client, run 15 in
`/run/media/tim/Games/pog-runs/lan-1/run15`): a client without a required mod, a client without POG Mod Check, and a
client with everything; F10 closes the window. The full record is P7 in POGContentLib's `ROADMAP.md`.

## Next

- [x] **Check before loading, with a choice** (the user's word, 2026-11-19): done the NGO-approval way, modelled on
      the game's own "missing dungeon content" retry (`NetworkContent`/`NetworkHandler` in the decompile): the offer
      rides in the game's connection data (`POGC1|` keys), the host refuses in `OnApprovingConnection` with its list
      as the reason, the player takes the refusal in `OnClientDisconnected` (where the game reads its own
      `POG_CONTENT:`), hands the game a retry key, and the game's retry coroutine (`ReconnectWithContent`, inlined
      by IL2CPP, so its `MoveNext` is hooked) is taken over. The game's popup asks; Join starts the client again
      with Accept, Cancel runs the game's `OnLostConnection` with `OnConnectionFailed` muted.
      **Run 16** (2026-11-19, `/run/media/tim/Games/pog-runs/lan-1/run16`, PC host, laptop client):
      - client without ShopDemo: popup in the main menu, nothing loaded (`client-prejoin-popup-3.png`); Esc: back to
        the menu, no "Disconnected" (`client-after-cancel-3.png`); F7 again, Space: in the hub 5 s later, host log
        "Player 7 joined with different mods", no window on the client (it saw the list in the popup);
      - client with every mod: no popup, "Mods match" both sides;
      - **host without POG Mod Check**: the client with the offer in its data is let in as before; 13 s later its
        window says the host did not answer (`client-host-without-modcheck.png`). Closes the "not run yet" item.
      - first build's popup: text too long (ran out of the box), the detail line over it, "Join anyway" over the
        Space glyph: fixed (two-line question, differences in the popup's detail line, button "Join");
      - second build: the game's own "Disconnected" popup replaced ours (race), because the game's
        `OnClientDisconnected` shows any reason that is not its own `POG_CONTENT:`: fixed by taking the refusal
        there.
      **Run 17** (2026-11-19, `run17`, same build 2da14ba87037): the host's side after Join. Client without
      ShopDemo, popup (`client-popup.png`), Space: the host's window "Player 2 joined with different mods / Extra:
      ShopDemo 1.0.0 (you have it, player 2 does not; everyone needs it)" in the top right (`host-window.png`); the
      client in the hub with no window (`client-in-hub.png`); no exception in the host's `Player.log`.
      Not verified: the EOS lobby join (only the LAN join through POGLanTest was run; the game's EOS join calls the
      same `StartClient` and the same approval); a gamepad; the IMGUI fallback when the menu popup is missing
      (never reached).
- [x] **A table of the differences, and a third kind, "same version"** (the user's word, 2026-11-19: show the
      host's version and the player's side by side; a mod like the timer need not be there, but if both have it, the
      same version). Rows "mod | host | you" ("—" where a side lacks it) in the popup (a text object of its own in
      the game's window, Roboto font, the window widened and put back after) and in the window after joining. Kinds:
      required, same version (author's mark `"same-version"` or setting `SameVersion`), own only (never shown). The
      offer now carries, per mod, a 4-digit tag of its name, the 8-digit fingerprint and its kind (no names); the
      host sends back only the copies the comparison needs. 97 unit tests; the agreement test (3 x 3000 pairs, now
      with three kinds) caught each of six planted mutations (the host ignoring the player's mark, the refusal
      leaving out required copies, the host treating its own same-version mods as own only, the comparison ignoring
      same-version, the refusal ignoring the player's marks, a tag without the name).
      **Run 18** (laptop alone, POGLanTest `Auto = "probe"`, `run18`): the popup with six rows of every kind and with
      one row (`client-preview-six.png`, `client-preview-one.png`); after it the game's own popup is 400 x 150 again
      with its error line (`client-preview-after.png`, sizes in `probe.log`).
      **Run 19** (PC host with POG Timer 1.1.1 marked, laptop with 1.1.0, `run19`, build 7d8c72aea56a): without
      ShopDemo, the popup lists ShopDemo 1.0.0 | — and POG Timer 1.1.1 | 1.1.0 (`client-popup.png`); Esc: the menu,
      clean (`client-after-cancel.png`); F7, Space: in the hub; the host's window shows the same two rows
      (`host-window.png`). With ShopDemo and without POG Timer: no question, "Mods match" on both sides. No exception
      in the host's `Player.log`.
      Not verified: more rows than fit (the "+N more" line, unit-tested only); a gamepad.
- [ ] **D13 The game's toasts cover the window** (run 15, laptop: an "Added to …" toast in the top right hid it
      until the toast left). The game's canvas draws over IMGUI. Move the window out of the toasts' corner.
- [ ] **D14 F10 is also POGSpawner's key**: closing the report also toggles the spawner panel when both are
      installed. Make the key a setting.
- [ ] Shop files without a DLL (they add slots, so they are network content) have no melon to list.

## Publishing

0.1.0 published on Thunderstore on 2026-11-19 (Largo/POG_Mod_Check, categories Mods and Tools), together with POG Timer
1.1.1, the first mod that marks itself "same-version". The packed DLL is the run 19 build (7d8c72aea56a). The icon is
`icon.png` (1024x1024, made by the user); the publish script (`_Modding/tools/ThunderStoreCLI.py`) packs it at
256x256 by itself.

- 0.1.1 (2026-11-19): the store page's developer section about any mod (POGContentLib one line at its end) and the
  changelog dated. The code is 0.1.0's: the decompiled DLLs differ only in the version strings.
