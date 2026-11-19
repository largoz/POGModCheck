# POG Mod Check

When a player joins a Pit of Goblin game, the host and that player compare their mods. If they differ in a way
that matters, a window in the top-right corner lists it:

- **Missing**: a mod the other side has that every player needs;
- **Extra**: a mod you have that every player needs and the other side lacks;
- **Different version** or **different build**: both have it, but not the same one.

The window does not stop the game. Close it with **F10** or its button. The same list goes to the MelonLoader log
on both sides, so a host can see what each joining player is missing.

## Which mods count

A mod is **required for everyone** when it adds network content (items, shop slots) through
[POGContentLib](https://github.com/LargoScript/POGContentLib): such a mod's objects simply do not appear for a
player without it. You can name more in the config. Every other mod is **own only** (graphics, a timer, a UI):
what one player runs is nobody else's business, and it is never reported.

## Settings

`UserData/MelonPreferences.cfg`, section `[POGModCheck]`:

- `Required`: more mods every player must have, comma-separated, names as in the MelonLoader console;
- `ShowWindow`: `false` keeps only the log.

## How it works

The lists (name, version, the first 12 hex digits of the DLL's SHA-256, required or not) travel over a Netcode
named message, `POGModCheck/List`, not over the Steam lobby, so it works in any session the game's networking
runs. A player whose host has no POG Mod Check is told so in the log; the window shows only when that player runs
mods everyone needs. Both sides need the mod for the comparison.

## Building

`dotnet build -c Release` (the game's assemblies are found through `GameDir`, by default the Gale profile; pass
`-p:GameDir=<folder with MelonLoader>` otherwise). Unit tests: `dotnet test tests/ModCheck.Tests`.
