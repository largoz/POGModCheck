# Changelog

## 0.1.0 (not released)

- First version: the host and a joining player swap mod lists over a Netcode named message; a window lists missing,
  extra and different required mods, closes with F10 or its button, and the log gets the same list.
- A mod's author can mark it required for everyone with a standard attribute,
  `[assembly: System.Reflection.AssemblyMetadata("POGModCheck", "required")]`, without referencing this mod.
- The host names a joining player that sends no mod list within 15 seconds (no POG Mod Check there).
- The log says which of your mods every player needs and why; reports stack in the window.
