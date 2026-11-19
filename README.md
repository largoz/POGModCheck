# POG Mod Check

When a player joins a Pit of Goblin game, the host and that player compare their mods. If they differ in a way
that matters, a window in the top-right corner lists it:

- **Missing**: a mod the other side has that every player needs;
- **Extra**: a mod you have that every player needs and the other side lacks;
- **Different version** or **different build**: both have it, but not the same one.

The window does not stop the game. Close it with **F10** or its button. The same list goes to the MelonLoader log
on both sides, so a host can see what each joining player is missing.

## Which mods count

A mod is **required for everyone** when:

- it adds network content (items, shop slots) through [POGContentLib](https://github.com/LargoScript/POGContentLib):
  such a mod's objects simply do not appear for a player without it (found by itself);
- its author marks it so (see below);
- you name it in the config.

Every other mod is **own only** (graphics, a timer, a UI): what one player runs is nobody else's business, and it
is never reported. The log says which of your mods count and why.

## For mod authors

Nothing is needed: every MelonLoader mod already has a name and a version, and that is what is compared. If every
player must run your mod (it changes the rules on both sides, say) and it does not add content through
POGContentLib, add one line anywhere in it:

```csharp
[assembly: System.Reflection.AssemblyMetadata("POGModCheck", "required")]
```

This is a standard .NET attribute: your mod needs no reference to POG Mod Check and runs the same without it.

## Settings

`UserData/MelonPreferences.cfg`, section `[POGModCheck]`:

- `Required`: more mods every player must have, comma-separated, names as in the MelonLoader console;
- `ShowWindow`: `false` keeps only the log.

## How it works

The lists (name, version, the first 12 hex digits of the DLL's SHA-256, required or not) travel over a Netcode
named message, `POGModCheck/List`, not over the Steam lobby, so it works in any session the game's networking
runs. Both sides need the mod for the comparison. A side that hears nothing within 15 seconds says so in the log:
a player whose host has no POG Mod Check, or a host whose joining player has none (named by its player number).
The window shows it only when you run mods everyone needs; otherwise nothing of yours can be missing over there.
Several reports stack in the window, so a second player joining does not hide the first one's.

## Building

`dotnet build -c Release` (the game's assemblies are found through `GameDir`, by default the Gale profile; pass
`-p:GameDir=<folder with MelonLoader>` otherwise). Unit tests: `dotnet test tests/ModCheck.Tests`.
