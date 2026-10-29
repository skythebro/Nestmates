Nestmates 1.0.0 - online co-op for IRON NEST: Heavy Turret Simulator
=================================================================================

Tested against: game build 25664742 (Steam), BepInEx 6.0.0-be.785 IL2CPP x64.

Install (every player)
  1. Back up your saves: %USERPROFILE%\AppData\LocalLow\<game company>\<game>\ (copy the folder somewhere).
  2. Install BepInEx 6.0.0-be.785 "Unity IL2CPP win x64" from the official BepInEx builds site into
     <game dir> and start the game once (BepInEx generates its files; the first start is slow).
  3. Remove any other co-op mod from <game dir>\BepInEx\plugins (two co-op mods at once will fight).
  4. Copy the BepInEx folder from this zip into <game dir> (it adds BepInEx\plugins\Nestmates\).
     Had a test build called "IronNest Co-op"? Delete <game dir>\BepInEx\plugins\IronNestCoop\ first (the two
     will not load together). Your settings and ban list are taken over on the first start.

Play
  - Press F7 in game for the Nestmates window (or Settings -> Multiplayer -> "Nestmates menu").
  - Host: Play tab -> Host (friends only or invite only). Invite from the list, or share the lobby code.
  - Join: accept the Steam invite, pick the friend from "Friends hosting", or paste the lobby code.
  - The host chooses the mission; everyone loads into it. You can also join a mission in progress.
  - Settings tab: your colour, name tags, crew list, your own cat, join/leave messages; the host rules.
  - Keys 1-8: emotes on your figure for the others (wave, point, thumbs up, salute, shrug, cheer, nod, shake head).
  - Streamer mode (on): the lobby code, join key and addresses show as dots; Copy still works, Show reveals them.
  - Steam and Discord status: your friends see the mission and how long you have played it (both can be turned
    off in Settings; where you are in the turret is only added if you turn that on).

What is shared (host decides, everyone sees the same)
  enemies and their moves, mission flow and timers, teleprinters (each player reads them in their own language) and
  notifications, the turret's aim and every gun (reload steps, cylinders, powder, shots with the host's exact flight),
  every dial, slider, lever and button (a guest's press runs on the host), the diesel engine and valves, light
  switches and other on/off fittings, the espresso machine, the record player (record, play/stop, track, position),
  fire-mission cards (printed once, same card for everyone; host option: only for the computer operator), map tokens
  and other paperwork, map drawings, the requisition console (the host's punchcard deck), unlocked records and
  decorations (the host's), one cat per player, player avatars. No pause in a shared world: menus open, time runs on.
  You can join a mission that is already running.

Personal on purpose
  your clipboard and notepad (they hang off your own view), your volume, your cat's AI, the newspaper.
  Guests keep their own campaign progress: finishing a mission with the host saves it as completed in their own
  profile too, and their own punchcards keep their own uses, minus what was spent in the shared mission.

Known gaps in this build
  - A guest in another language sees the host's text in a few places (notifications are fine; teleprinters are
    re-rendered; anything else printed as finished text may stay in the host's language).

Settings: <game dir>\BepInEx\config\Nestmates.cfg (also in game: Settings -> Multiplayer).
Logs: <game dir>\BepInEx\Nestmates.log (please attach it to any bug report).
License: MIT (see LICENSE). Independent fan project, not affiliated with the game's developers.
