# URAH_Net

A BepInEx 5 plugin for **Knights of Honor II: Sovereign** that fixes multiplayer lobby problems of the
base game. Needs no mod and works in vanilla; players with and without it can play together.

- **Grey players heal by themselves.** In the lobby players turn grey at random and can not join any
  more, and trying again ends in endless joining until a restart: a dead connection stays in the
  connection pool and blocks every new one, and a failed connect is never retried. URAH_Net throws
  the dead connection away and reconnects after 2, 4, 8, 16, then every 30 seconds.
- **Lobbies with several mods show their mods.** The lobby browser read the host's mod list through a
  128-character buffer, so with two mods with longer names it arrived cut off and nobody could join.
- **Mod order does not matter.** Starting a game compares the players' mod lists as text, in the order
  the mods were switched on; the same mods in a different order gave `client_version_mismatch`.
  URAH_Net sends the list sorted (helps when everyone runs it).
- **Reordered buildings show right away.** Moving buildings to other slots reached the host, but the
  new order was never sent back, so the other players only saw it after the next load. The host now
  sends it (only the host needs URAH_Net for this).
- **Diagnostics** (log only, `[Diagnostics] Enabled`): heartbeat gaps per player, connections that stop
  sending, held network locks, send queues and main thread stalls, every campaign join with the
  owner's connection state, and errors of the lobby slot actions. After a drop or a hang, the
  `BepInEx/LogOutput.log` of everyone involved shows where it broke.

Every fix only acts on the machine that runs it, so everyone in a lobby should have it. Log lines start
with `[URAH_Net]` in `BepInEx/LogOutput.log`; settings in `BepInEx/config/urah.koh2.net.cfg`.

## Install

BepInEx 5 in the game folder, then `URAH_Net.dll` into `BepInEx/plugins/URAH_Net/`.
To remove it, delete that folder.

## Building

```sh
git clone --recursive <this repo>      # external/URAH_shared is a submodule
cd plugin && dotnet build -c Release -p:Deploy=true
```

Compiles against your own game install with BepInEx 5 (`GameDir`, default: Steam on Linux).
