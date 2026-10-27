# URAH_Net

A BepInEx 5 plugin for **Knights of Honor II: Sovereign** that fixes multiplayer lobby problems of the
base game. Needs no mod and works in vanilla; players with and without it can play together.

- **Grey players heal by themselves.** In the lobby players turn grey at random and can not join any
  more, and trying again ends in endless joining until a restart: a dead connection stays in the
  connection pool and blocks every new one, and a failed connect is never retried. URAH_Net throws
  the dead connection away and reconnects after 2, 4, 8, 16, then every 30 seconds.
- **Reset button for players who stay grey.** A button right of the lobby's "Copy Code" (and
  `Ctrl+Shift+F9`, `[Session] ResetKey`, in the main menu or a lobby) drops every lobby connection,
  closes the Steam P2P sessions and connects again on fresh ones. The game itself only ever closes
  single channels, so a broken Steam session stays broken until a restart. The button is active once
  a player of the lobby lost the connection. Everyone in the lobby is grey for about 6 seconds.
- **Connections keep working in long sessions.** The game gives every new connection the next channel
  number for the whole session, but its native P2P read only reaches channels up to 255. Above that the
  read gets nothing and every connection dies after 5 seconds, again with each reconnect (the log
  fills with `message not completely read!`). URAH_Net keeps the channels at 255 or below and wraps
  around to free ones. A player without it may still ask for a channel above 255 if their game saw
  one of ours earlier; that connection only works again after they restart.
- **Lobbies with several mods show their mods.** The lobby browser read the host's mod list through a
  128-character buffer, so with two mods with longer names it arrived cut off and nobody could join.
- **Mod order does not matter.** Starting a game compares the players' mod lists as text, in the order
  the mods were switched on; the same mods in a different order gave `client_version_mismatch`.
  URAH_Net sends the list sorted (helps when everyone runs it).
- **Reordered buildings show right away.** Moving buildings to other slots reached the host, but the
  new order was never sent back, so the other players only saw it after the next load. The host now
  sends it (only the host needs URAH_Net for this).
- **Lobbies open after a long game.** After a longer match the icons of the player slot actions (kick,
  invite, close) fail to build, which stops creating a new lobby and shows an existing one without the
  map. The icon then carries the action bar's `UIActionIcon` instead of the lobby's class of the same
  name. URAH_Net catches the error, tries the icon once more and, when that fails, makes it work with its
  own click handler, so the host can still kick, invite or close a slot. Why it happens only after a long
  game is still unknown; the log says what the icon looked like.
- **Diagnostics** (log only, `[Diagnostics] Enabled`): heartbeat gaps per player, connections that stop
  sending, held network locks, send queues and main thread stalls, every campaign join with the
  owner's connection state, and P2P reads whose size does not match the announced one (the game's
  `THQNO.ReadP2PPacket() message not completely read!`) with sender, channel and both sizes. After a drop or a hang, the
  `BepInEx/LogOutput.log` of everyone involved shows where it broke.

Every fix only acts on the machine that runs it, so everyone in a lobby should have it. Log lines start
with `[URAH_Net]` in `BepInEx/LogOutput.log`; settings in `BepInEx/config/urah.koh2.net.cfg`.
`[General] Enabled` switches the whole plugin on and off right away, no restart needed.

## Install

BepInEx 5 in the game folder, then `URAH_Net.dll` into `BepInEx/plugins/URAH_Net/`.
To remove it, delete that folder.

## Releases

Built and signed releases are in [URAH_koh2_releases](https://github.com/URAHDangerzone/URAH_koh2_releases/releases?q=URAH_Net&expanded=true): [`URAH_Net-v<version>`](https://github.com/URAHDangerzone/URAH_koh2_releases/releases?q=URAH_Net&expanded=true) is this plugin alone
(`BepInEx/plugins/URAH_Net/`), [`URAH_Starter`](https://github.com/URAHDangerzone/URAH_koh2_releases/releases?q=URAH_Starter&expanded=true) bundles BepInEx 5 with URAH_Loader, URAH_Net and URAH_PluginManager.
Every release is signed with the URAH release key; [URAH_koh2_releases](https://github.com/URAHDangerzone/URAH_koh2_releases) explains how to check it.

## Building

```sh
git clone --recursive <this repo>      # external/URAH_shared is a submodule
cd plugin && dotnet build -c Release -p:Deploy=true
```

Compiles against your own game install with BepInEx 5 (`GameDir`, default: Steam on Linux).
