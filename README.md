# Nestmates

Online co-op for **IRON NEST: Heavy Turret Simulator**: two or more players crew one turret in the host's
world. It's a BepInEx 6 (IL2CPP) plugin, a clean-room implementation under the MIT license. No game files are included.

Tested against Steam build 25664742 (Unity 6000.3.21f1, IL2CPP) with BepInEx 6.0.0-be.785.

## How it works

- **Host-authoritative.** Only the host runs the mission logic: enemies, mission graph, impacts, the teleprinter
  orders, the economy. Guests' copies of that logic are switched off, and guests mirror the host's results.
- **Guest actions are forwarded.** A guest's button press, lever pull, dial or slider drag runs on the host. The
  result comes back to everyone the same way as the host's own actions.
- **One message per game event.** Each module (`src/IronNestCoop/Sync/*.cs`) hooks one game system with Harmony
  and replays it through the game's own methods on the other side. Each module also has a join snapshot, so
  late joiners get the full current state.
- **Transports:** Steam networking (lobbies, invites, relay) for play, and TCP loopback for the automated tests.

## What is shared

Enemies and their movement, mission flow, timers and the clock. Teleprinters, each rendered in the reader's own
language, and notifications. The turret's aim and every gun: reload steps, cylinders, powder charges, and shots
with the host's exact flight. Every dial, slider, lever and button. The diesel engine and valves, light switches
and other on/off fittings, the espresso machine, and the record player (record, play/stop, track, position).
Fire-mission cards, map tokens and paperwork, map drawings, the requisition console, and unlocked records and
decorations. One cat per player, and player avatars with name tags. Players can join a mission already in
progress. The game doesn't pause during a session, unless every player has the menu open.

Extras: emotes on keys 1-8, a crew figure that points at the tactical map and works the levers it holds,
join/leave messages, Steam and Discord status (both can be turned off), and a streamer mode that hides lobby codes.

These stay personal by design: your clipboard and notepad, volume, and your cat's AI. Your campaign progress
is your own, too. Finishing a mission with the host records it in your profile, and your own punchcards keep
your own use counts, minus what the shared mission spent.

## Build

Requirements: .NET 6+ SDK, and a copy of the game with BepInEx 6.0.0-be.785 (IL2CPP) that has been started
once, so the interop assemblies exist.

    dotnet build src/IronNestCoop -c Release -p:GameDir="<game dir>"

The plugin is `src/IronNestCoop/bin/Release/net6.0/Nestmates.dll` plus `Nestmates.Net.dll`. Copy both
to `<game dir>\BepInEx\plugins\Nestmates\`. Settings are in `BepInEx\config\Nestmates.cfg`, the log in
`BepInEx\Nestmates.log`. (The test builds were called IronNest Co-op, in `plugins\IronNestCoop\`: delete that
folder; the settings and ban list are taken over on the first start. The source keeps the `IronNestCoop` folder
and namespace names.)

## Tests

- `dotnet run --project tests/IronNestCoop.Net.Tests`: protocol and transport unit tests.
- `python -I tools/foldcheck.py <cpp2il-cs-dir> src/IronNestCoop`: fails if a Harmony target is too short to
  detour or shares its native code with other methods (identical-code folding).
- `python -I tools/coopqa.py run <scenario>`: starts two copies of the game, never the Steam install, connected
  over loopback. In-game bots (`src/IronNestCoop/QA`) play both sides and compare the two worlds. Each copy uses a
  sandboxed save folder, and the harness restores PlayerPrefs afterwards.
  - Scenarios: connect, mission, entities, world, avatars, controls, engine, music, cats, items, markers,
    unlocks, pause, requisition, toggles, espresso, language, gunnery, latejoin, progress, rejoin, maprejoin,
    magazine, paint, dice, scout, events, gametab, cups, emotes.
  - A bot fails on any mod error, including exceptions thrown inside Harmony patches (taken from BepInEx's log).
  - Mutation twins (`-coop-nopatch=<PatchClass>`, `-coop-mutant=<name>`) switch one piece of sync off to prove a
    scenario can fail.

## Known issues

- A guest in another language may see the host's language in places that print finished text. Teleprinters
  and notifications are translated.

## License

MIT, see [LICENSE](LICENSE). Independent fan project, not affiliated with the game's developers.
