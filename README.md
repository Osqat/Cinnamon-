# Cinnamon

BepInEx utility library for Ultimate Chicken Horse. Provides auto-update and cursor overlay for dependent mods.

## Installation

1. Drop `Cinnamon.dll` into `BepInEx/plugins/Cinnamon/`
2. Launch the game once — the update patcher installs itself automatically

## For Mod Developers

Add to your `Plugin.cs` to opt into auto-update:

```csharp
[assembly: Cinnamon.AutoUpdate("yourGitHubUsername/yourRepo")]
```

Then create a GitHub Release tagged `v1.0.1` with your compiled DLL attached (must be named `YourMod.dll`). Users get the update on next launch.

When releasing a new version, bump both:
```csharp
[BepInPlugin("...", "...", "1.0.1")]
[assembly: System.Reflection.AssemblyVersion("1.0.1")]
```
