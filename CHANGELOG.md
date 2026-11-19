# Changelog

## 0.1.1 (2026-11-19)

- The store page: the section for mod developers is about any mod (one line says how much it matters to the other
  players); POGContentLib is one line at its end. The 0.1.0 changelog has its date instead of "not released".
- The mod itself is the same as 0.1.0.

## 0.1.0 (2026-11-19)

- The check before loading: a player whose mods differ from the host's where it matters sees the game's popup in
  the main menu and chooses Join or Cancel; nothing loads until it chooses. Setting `AskBeforeJoin`.
- The differences show as a table, the mod's name, the host's version and yours ("—" where a side lacks it), in
  the popup and in the window after joining.
- A third kind besides "required" and "own only": **same version**, nobody has to have it, whoever has it needs the
  host's version (POG Timer 1.1.1 marks itself so). Marked with
  `[assembly: System.Reflection.AssemblyMetadata("POGModCheck", "same-version")]`, or setting `SameVersion`.
- First version: the host and a joining player swap mod lists over a Netcode named message; a window lists missing,
  extra and different required mods, closes with F10 or its button, and the log gets the same list.
- A mod's author can mark it required for everyone with a standard attribute,
  `[assembly: System.Reflection.AssemblyMetadata("POGModCheck", "required")]`, without referencing this mod.
- The host names a joining player that sends no mod list within 15 seconds (no POG Mod Check there).
- The log says which of your mods every player needs and why; reports stack in the window.
