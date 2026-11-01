# recharge-mods

Mods for [Recharge](https://github.com/SumDumIdiut/recharge), each a self-contained C# project built against `Recharge.ModApi.dll`:

| Folder | Mod |
|---|---|
| `recharge-multiplayer` | DOTnet - multiplayer with ghosts, chat, and Normal / Co-op modes. `server/` is the shared relay (`node server.js`, `PORT` env, default 7777). |
| `recharge-gun` | Gun - a free "Unlock gun" box at your spawn; then an aimed pixel-art gun with bullets that break tiles and recoil physics (shoot down to gun-jump). Settings row on the pause menu's mod page; `RECHARGE_GUN_SELFTEST=1` runs a scripted in-game self-test. |
| `recharge-example` | Example Mod - a tabbed reference panel exercising the ModApi. |
| `_template` | Starter for a new mod (skipped by the loader build). |

Navigator lives in [recharge-maps](https://github.com/SumDumIdiut/recharge-maps) and Skinmod in [recharge-skins](https://github.com/SumDumIdiut/recharge-skins).

Recharge pulls this repo into its mods folder when you install a mod, then compiles it. To build by hand, point the loader script at a folder containing this repo:

```
pwsh loader/build-loader.ps1 -GameDir "<GameDir>" -ModsDir "<folder containing recharge-mods>"
```
