# Spawn Visualiser

A BepInEx mod for Valheim that shows where creatures can spawn around you. Press **J** to toggle it.

## What you see

| Colour | Meaning |
|---|---|
| 🟥 Red | Hostile creatures can spawn here **right now** |
| 🟧 Orange | Hostiles can spawn here, but only under other conditions (night, certain weather) |
| 🟦 Blue | Only passive animals (deer, fish, birds…) can spawn here |
| 🟩 Green | Hostiles *would* spawn here, but your base (workbench, campfire…) is blocking them |
| *(nothing)* | Nothing can spawn: blocked by buildings, trees or rocks, terrain too steep, wrong biome, etc. |

* **Rings:** the bright ring at 40 m marks where world spawns stop: mobs never spawn closer to you than this. The faint ring at 80 m is the outer edge of where spawns are attempted.
* **Labels** over nests, spawners and fixed creature spawn points (dungeons, camps) show what they spawn and how often.
* **Legend** lists the creatures that can spawn nearby, the time of day and weather, and whether a raid is active.

The overlay uses the same checks as the game's `SpawnSystem.IsSpawnPointGood`: biome, biome area, altitude, slope, forest, lava, ocean depth, distance from world centre, blocking objects and player-base protection. Spawns locked behind boss kills are left out until you unlock them. Spawns from an active raid are included.

## Config

The config file is `BepInEx/config/lachee.spawnvisualiser.cfg`, and you can also change it with Configuration Manager (F1). Settings:

* the toggle key
* the scan radius (16–128 m) and tile size
* how often the overlay refreshes and how many tiles it checks per frame
* opacity, and each overlay category on or off
* rings, labels and the legend, plus where the legend sits
* every colour

## Building

You need the .NET SDK (any recent version) and Valheim with BepInEx installed.

```sh
dotnet build -c Release                                      # builds and copies to the Amethyst mod folder "Spawn Visualiser"
dotnet build -c Release -p:AmethystModDir=/some/other/folder # different mod folder
dotnet build -c Release -p:DeployToGame=false                # build only
```

After building, enable **Spawn Visualiser** in Amethyst Mod Manager (refresh first if it isn't listed) and deploy.
