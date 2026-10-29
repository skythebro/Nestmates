# IronNestCoop: pre-release code quality audit (v2, re-audit)

| | |
|---|---|
| Date | 2026-10-28 (second pass, same day as v1) |
| Version audited | 0.1.0 (released as 1.0.0) |
| Repo state | `.git` present, still **zero commits** |
| Method | Six read-only Claude Haiku auditors, one per area. Each re-checked every v1 finding and searched for new issues. The coordinating agent then checked the highest-severity claims against the source. |

Paths are relative to `src/IronNestCoop/` unless they start with `src/`, `tests/`, `tools/` or `dist/`. Line numbers are current as of this pass.

**Markers**
- ✅ **VERIFIED**: the coordinator read the code and confirmed the issue.
- **No marker**: an auditor reported it from evidence, but no one re-checked it. Confirm before fixing.
- **UNSURE**: depends on game internals. Needs a runtime or QA check.

## Verdict

**Not release-ready.** The codebase roughly doubled (~16k lines) since v1, and real progress has been made:

- The network layer's crash and memory bugs are mostly fixed.
- The F7 soft-lock is fixed.
- Names are sanitized, and the Steam receive path is size-capped.
- Host rules can't be changed by guests.

Four things still block a public release:

1. **The direct-connect TCP host is still exposed.** It binds to all interfaces, has no password, and now also creates one thread per connection with no limit.
2. **State carries over from one session into the next.** This is now the most widespread bug class. The root cause is that `MissionFlow.OnSessionStart` resets `Epoch = 0`, so every cache keyed on the epoch matches the previous session's values.
3. **QA, mutant and dev code ships in the release DLL**, with no compile-time gate.
4. **The Steam lobby path is still untested between two real accounts.**

Network tests: `dotnet run --project tests/IronNestCoop.Net.Tests` gives **PASS 20/20**, but none of the 20 is new. None of the defences added since v1 has a test.

---

## 0. Fixed since v1

| v1 item | Evidence |
|---|---|
| B1 nested-fragment stack overflow ✅ | `src/IronNestCoop.Net/Session/Session.cs:312` rejects a reassembled Fragment |
| B3 fragment memory (mostly) | 16 MB per-message cap at `Fragmenter.cs:18`, `:78`; `long Bytes` at `:26`; piece size checked against the transport at `:63`; 60 s `Expire` at `:95-102`, called from `Session.cs:238`. See N-NET2 for what remains. |
| B3 Steam receive allocation ✅ | `Steam/SteamTransport.cs:79` drops anything over 512 KB before allocating; `SteamApi.cs:60` sets 1 MB |
| B4 Tick swallowing exceptions | `Session.cs:233-235` catches per packet; `:496-498` catches per handler; slot bounds at `:153`, `:189`, `:373`, `:484` |
| B5 F7 soft-lock ✅ | `UI/Layout.cs:170-176` `EndFrame` drops focus from fields that were not drawn; `CoopUI.cs:93` clears focus on close |
| Ban-file injection / unsanitized names | `Session.cs:304`, `:328`, `:443` call `Sanitize`, which strips control characters and `<>` (`:502-510`). Cat names are still unsanitized; see Medium. |
| Name and Info size limits | `ClampInfo` on every send path (`Session.cs:111`, `:121`, `:129`, `:514-519`) |
| Guest host-clock offset | `Session.cs:377` sets `_haveOffset`. This also fixes Music position correction. |
| NaN Pong / roster RTT | `Session.cs:391`, `:444` |
| Surrogate-pair truncation | `Session.cs:509` |
| TCP accept stall | `TcpTransport.cs:98` greets on a separate thread with a 5 s deadline. This fix introduced N-NET1. |
| TCP id eviction | `TcpTransport.cs:112-114` refuses a duplicate id instead of evicting the existing peer |
| TCP Connect socket leak | `TcpTransport.cs:56-59`, `:69` |
| TCP send/inbox | 3 s send timeout with disconnect (`:63`, `:107`, `:179`); 64 MB per-connection queue cap (`:23`, `:137`) |
| Accept-loop back-off, bulk copy, ITransport doc | `TcpTransport.cs:95`; `Fragmenter.cs:84`; `ITransport.cs:8` |
| Items session reset | `Sync/Items.cs:60` clears `_cardOwner`, `_copies`, `RemoteHeld`, `_known`, `_heldSent`, and more |
| `GUI.matrix` not restored | `UI/CoopUI.cs:57` sets it, `:62` restores it in a `finally` |
| Host rules spoofing | `HostRules.OnRules` accepts the rules only from the host (`HostRules.cs:110`), and `Session.cs:479` enforces the message origin. The UI is read-only for guests. |
| Music rotation NaN | `Sync/Music.cs:163-168` |
| Marker erase not sent | The 0.5 s sweep in `Sync/Markers.cs:67-89` now sends the removal |
| Controls hover epoch/ready | `Sync/Controls.cs:413-414` (presses are still unchecked; see High) |
| Timer change detection (partial) | `Sync/WorldEvents.cs:91-93` |

---

## 1. Must fix before release

### B2 ✅ (still open, now worse) Direct-connect TCP host: unauthenticated, on all interfaces, unbounded threads
- **Where:**
  - `Core/Coop.cs:159`: `HostTcp` still passes `IPAddress.Any`.
  - `UI/CoopUI.cs:225`: the Host button is in the normal Play tab.
  - `Session.cs:328-337`: Hello checks only the protocol version, kick list, bans and free slots.
- **New issue, thread and socket exhaustion** (`src/IronNestCoop.Net/Transport/TcpTransport.cs:94-111`):
  - Every accepted socket gets its own `new Thread`.
  - After the 8-byte greeting, `ReadTimeout = Infinite`. A socket that never sends Hello is never closed, because Session only closes peers it has refused (`Session.cs:350`).
  - `Thread.Start` sits outside the try block.
  - Someone who can reach the port can open sockets until thread creation fails, which takes down the host process. The exact limit is UNSURE.
- **Ids are still chosen by the client** (`TcpTransport.cs:112`):
  - A banned player can rejoin by picking a new id, so bans are bypassable (`Core/Coop.cs:214`, `UI/CoopUI.cs:289`).
  - A guest can claim id 1, which is the host's id (`Coop.cs:159`, `:169`).
  - The UI still offers a "lasting" Ban on TCP peers (`CoopUI.cs:277`).
- **Errors are invisible to the user:**
  - `HostTcp` has no try, so a port-in-use error is never shown (`Coop.cs:156-160`).
  - `int.Parse` on the port still sits outside the try (`Coop.cs:165`).
  - Join still blocks the game thread for up to 5 s (`TcpTransport.cs:50`, called from OnGUI).
- **Fix:** remove direct-connect from release builds. If it stays:
  - Bind to loopback or a chosen LAN address.
  - Require a passphrase in Hello.
  - Cap the number of pre-Hello connections and add a Hello deadline after the greeting.
  - Reject `id == LocalId`.
  - Hide Ban for TCP sessions.
  - Wrap Host and Join in try and report errors through `Fail()`.
  - Connect off the game thread.

### B6 ✅ (still open, wider) State carries over between sessions because the epoch restarts at 0
- **Root cause:** `Sync/MissionFlow.cs:70-72` sets `Epoch = 0` in `OnSessionStart`, so the first mission of every session is epoch 1. Any cache that is cleared only "when the epoch changes" survives into the next session. Only Items is now fully reset.

| Module | State | Status |
|---|---|---|
| ✅ Gunnery | `_pending`, `_fallbackAt`, `_awaitingSince` (`Gunnery.cs:34-36`) | The file has **no** `OnSessionStart`/`OnSessionEnd`. A leftover queued shot pairs every later host shot one entry late. |
| ✅ WorldEvents | teleprinter `History` (`:40-47`), `_deliveries` (`:657`), `Spawns` (`:127-128`), `_timerEpoch` (`:277`) | The file has **no** `OnSessionStart`. Delivery counters continue (host "X#4" vs a fresh guest's "X#1"), so ItemIds differ and placements are dropped (`Items.cs:331`). Late joiners receive the previous session's teleprinter text and PrefabSpawn replays. |
| MissionFlow | `_pendingStart` (`:33`) | Not cleared at `:70-78`, so the old start is applied in the new session (`:120-124`). |
| WorldEntities | `_announced` (`:164-171`) | Cleared only when the epoch changes. |
| ✅ SharedDice | `_counts` (`SharedDice.cs:53-59`) | Epoch-keyed and no `OnSessionStart`. Also, `Next()` is called before the in-session guard (`:110`, `:117`), and late joiners start counting at 0. |
| ✅ Requisition | `_usesEpoch`, `_firstUses`, `_spent` | Reset only on epoch change (`:123`), not in `OnSessionStart` (`:40`). Stale counts can write the wrong `RemainingUses` into a guest's save (`:290`). |
| ✅ Music | `_lastRecord`, `_lastPlaying`, `_localChangeUntil` | `OnSessionStart` (`:41`) resets only `_have` and `_lastTrack`. |
| Music / Espresso / Machinery (guest) | `_have`, `_host`, `_haveHost` | Kept **across missions** within a session. `Music.GuestTick` (`:177-180`) has no `WorldReady.LocalReady` gate, so a guest loading mission N+1 can put the old record on and start playback before the host's snapshot arrives. Espresso (`:116-119`) and Machinery (`:122-124`) apply old gauges the same way. (HIGH, trigger UNSURE) |
| WorldReady / WorldEvents | `_readyEpoch`, `_lastRp`, `_lastCharges` | No visible effect today, but still not reset. |
| Controls | `HeldSliders` | Cleared on session start (`:95`), but there is no `OnSessionEnd`. A slider being dragged remotely stays blocked in single-player. |

- **Fix, two parts:**
  - Give every module with caches an `OnSessionStart` and `OnSessionEnd` reset. Consider having the `SyncModule` base class require one.
  - Make epoch keys unique across sessions: combine the epoch with a per-session nonce, or never reset the epoch counter.

### QA, mutant and dev code is compiled into the release DLL
- **Where:** there is no `#if` or `DefineConstants` anywhere. The zip's `IronNestCoop.dll` contains the Bot, Wiring, UiProbe, Fingerprint and DevTools types.
- **What ships live:**
  - `-coop-mutant=` names that disable sync safeguards: `HostRules.cs:65` `norules`, `Items.cs:57`, `Music.cs:38`, `Controls.cs:111`, `Requisition.cs:140`, `SharedDice.cs:25`.
  - `-coop-nopatch` (`Plugin.cs:19-30`).
  - `-coop-uiprobe`, re-read from the command line every frame (`CoopRunner.cs:76`).
  - An always-on 30 s Probe (`CoopRunner.cs:65-69`).
  - `Pointer.TestPress` (`UI/Pointer.cs:23`).
  - The `-coop-bridge` switch is still dead (`LaunchArgs.cs:31`, `:56`; `IsTest` at `:72`).
- **Gating that does exist:**
  - `-coop-bot` requires `-coop-sandbox` (`QA/Bot.cs`).
  - DevTools is host-only and off by default (`[Debug] DevTools`, `Settings.cs:17`). No network path reaches it.
- **Related:** the Sandbox Harmony patches are still installed on every launch (`Plugin.cs:43-49`). They do check internally that the sandbox is active.
- **Fix:** put `QA/`, `Dev/`, the mutant and nopatch paths, and the test hooks behind `#if COOP_QA`, or ship them as a separate QA build.

### B7 The Steam lobby path has never been tested between two real accounts
- **Where:** `README.md:57-60` and `dist/README.txt:35-36` disclose this.
- **Why it blocks:** Steam is the default way to play.
- **Fix:** run a two-account test (see section 7), or label the release experimental.

---

## 2. High

### Networking
- **N-NET1:** TCP thread and socket growth. Covered under B2.

### Sync: trust and desync
- ✅ **Paint trusts guest brush settings** (`Sync/Paint.cs:74-82`).
  - Size, scale, softness and colour from the guest are applied to the host's mesh with no clamping or NaN check, then relayed to everyone.
  - There is no per-slot rate limit, and each message can carry up to 4,000 dabs.
  - A NaN or huge brush value may stall `PaintAtUV` on every machine (UNSURE).
  - Fix: clamp to the UI ranges, reject non-finite values, and rate-limit per slot.
- ✅ **Animators force a full scan for every unknown id** (`Sync/Animators.cs:214`).
  - `_nextScan = 0; Scan();` runs once per unknown id, per message, at up to 20 Hz. Each scan is a `FindObjectsOfType(Animator)` plus a `ScenePath` call per animator.
  - A guest whose copy of an animator is inactive (skipped at `:87`) rescans on every host update.
  - Same pattern in `Visibility.cs:137`.
  - Fix: rescan at most once per second and cache misses.
- ✅ **Animator parameter cap misaligns the parse** (`Animators.cs:210-212`). When `pc` is above 256, the loop reads only 256 entries and does not skip the rest, so every later field in the message is read from the wrong offset. Fix: read all entries and discard the extras, or reject the message.
- **Animator state catch-up fights the game** (`Animators.cs:153-156`, `:244-264`, UNSURE which animators are affected).
  - State is sent for *every* animator, although the doc at `:19-20` says triggers only.
  - `CatchUpStates` calls `Play(state, 0, 1f)` on any mismatch after 0.6 s. Float parameters are not mirrored (`:94`), so animators driven by floats snap back over and over.
  - Fix: limit state sync to animators that use triggers.
- **Replayed calls run with the mission-graph block lifted** (UNSURE how far this reaches).
  - `GraphGate.cs:16`: `Suppress` is false during every `Coop.Apply`.
  - Replays that go through `Coop.Apply` instead of `ApplyVisual`:
    - WorldEvents SceneNote, SubmitLines and SpawnImpactPrefab
    - `Machinery.cs:134-144`
    - `Espresso.cs:96`
    - `Controls.cs:81`
  - The comment at `WorldEvents.cs:693-695` ("guests never evaluate impacts") does not hold during replays.
  - Fix: use `ApplyVisual` for presentation-only calls, or use a separate flag for the gate.
- **Controls:**
  - **Slider drag sticks across a rescan** (`Controls.cs:259`, `:149-154`, `:231`, `:516`). `SendSnapshot` and `ButtonId` call `Rescan(true)`, which rebuilds the entries and loses `ReplayingDrag`.
  - **Presses have no epoch or ready check** (`OnClick` `:294-313`, `OnValue` `:271-292`). The host broadcasts with `Coop.Send` rather than `SendToReady` (`:254`, `:540`).
  - **A press on an unregistered control runs locally (fail-open)** (`:525-526`), which desyncs.
- **Cat and avatar builds leak on exception, with no back-off.**
  - The Cats catch block (`:231-235`) leaks `holder` and `go`. The Avatars catch block (`:449-453`) leaks a DontDestroyOnLoad root.
  - Both retry on every state message.
  - `CatHat` from the peer is still unvalidated (`Cats.cs:216`).
  - Fix: destroy on catch, validate the hat value, and back off.
- **Teleprinter alarm may hit the wrong printer** (UNSURE). `WorldEvents.cs:476` sends the raw `Printer` value, while ClearAlarm maps flags (`:487-489`).

### Robustness and performance
- **Harmony patch bodies without try/catch.** An exception here unwinds into the game's own code.
  - WorldEvents: `:370-410`, `:511-538`, `:546-555`, `:602-608`
  - Gunnery: `:356-371`, `:389-407`
  - Items: `:440-470`
  - WorldEntities: `:343-370`
  - Controls: `:440-459`, `:549-561`
  - Markers: `:209-213`, `:228-238`
  - Espresso: `:134-165`
  - Machinery: `:150-172`
  - Requisition: `:256-296`
  - Music: `:225-256`
  - SharedDice: 8 patches at `:67-181`. A throw here leaves the game's random state seeded.
- **Per-frame `FindObjectOfType` when the object is absent:**
  - `Music.cs:43-48`, called every frame from `:83` and `:179`, even on the map
  - `Cats.cs:62-67`, `:176`
  - `Espresso.cs:38-43`, which runs before its throttle
  - `Avatars.cs:133`, every 50 ms
- **Avatars `Table()` has no negative cache** (`Avatars.cs:216-231`). It scans every Renderer on each pose (20 Hz), and its cache key is the scene count only.
- **Music guest retries every tick** (UNSURE). `StartPlayback` and `AdvanceTrack` are re-issued every tick until the state matches (`:198-209`). A Debug string is also built every frame (`:192`), and Verbose defaults to true.

---

## 3. Medium

### Core / Steam / UI
- ✅ **Accepting a Steam invite while hosting ends the session for everyone** (`Steam/SteamJoinRequests.cs:36`). It calls `Coop.Leave()` with no confirmation. Fix: prompt the host, or ignore the request while hosting.
- ✅ **A connecting guest counts as the authority** (`Core/Coop.cs:82-86`). `IsGuest`/`IsHost` require `IsConnected`, so while a guest is still connecting `IsAuthority` is true and `HostRules.Mask` returns the guest's own settings. All `!Coop.IsGuest` gates then act as host (`GraphGate.cs:16`, `Gunnery.cs:442-445`, `MissionFlow.cs:73`, `Unlocks.cs:72`), and DevTools `Enabled` is true (`DevTools.cs:28`). UNSURE whether a mission can be running during that window. Fix: gate on `Session != null && !IsHost`, and use the default rules until the host's arrive.
- ✅ **Enter in the direct-connect field triggers the Steam code join** (`CoopUI.cs:210`, `:227`; `Layout.cs:136`). Both fields share one submit flag. Fix: give `TakeSubmit` a field id.
- **Cancel during a Steam create or join does not cancel** (`Coop.cs:244-248`, `:175-186`). The create callback still starts a session, and the lobby is left orphaned. Fix: use a generation token, and call `Leave()` on stale lobbies.
- **Begin() leaves the new lobby instead of the old one** (latent). `Coop.cs:179` assigns `Lobby` before `Begin` calls `Leave("restart")` at `:208`.
- **CursorRelease records a cursor lock it never took** (`UI/CursorRelease.cs:31-33`). When there's no broker it sets `_held` anyway, and never retries, so the world isn't frozen under the open window.
- **Errors only show inside the window** (`Coop.cs:235-236`). A normal host close shows guests a red "host ended the session (left)" (`Session.cs:408`).
- **One-click Ban and Kick** buttons sit 6 px apart, with no confirmation and no unban UI (`CoopUI.cs:289-290`).
- **Ban list:** a failed read caches an empty set for the rest of the run (`Core/Bans.cs:18`, `:28`). `Bans.cs:38` still writes the raw name into the file.
- **Steam auto-init gives up after 90 s** (`CoopRunner.cs:42`).
- **Reliable Steam sends** still `Thread.Sleep(5)` up to three times on the main thread, then drop silently (`SteamTransport.cs:39-51`).
- **Timer rule rounds to 25 % steps** (`HostRules.cs:49`, `:58`, `:121`). A config value of 1-12 turns the timer *off* for everyone. `CoopUI.cs:385` compares against the raw value, so no option is highlighted. Fix: use `AcceptableValueRange` and store the rounded value.
- **DevTools:**
  - Ctrl+Shift hotkeys fire while a co-op text field has focus. In the hex lobby-code field, C deals cards and E toggles the engine (`DevTools.cs:58-60`).
  - A failed save backup only logs, but `Used` is still set. Backup folders are never pruned (`:87-110`).
- **Clicks may also reach the game's own UI under the window** (UNSURE). `UI/Pointer.cs:56-62` only marks the event as consumed for IMGUI.

### Networking
- **N-NET2: fragment budget is per partial, not per peer** (`Fragmenter.cs:16`, `:66-81`). That is 8 × 16 MB, about 128 MB per guest, held for 60 s and renewable. A duplicate of a completed id opens a new partial. Fix: a per-peer byte budget, and reject ids at or below the last completed one.
- **N-NET3: PlayerInfo amplification** (`Session.cs:301-306`). Each PlayerInfo triggers a full roster broadcast with no rate limit: one 4 KB packet in, up to about 80 KB out. Fix: coalesce to one broadcast per Tick.
- **N-NET4: a slow reader can hold the game thread** (`TcpTransport.cs:167-180`, UNSURE). `SendTimeout` limits each write, not the whole send.

### Sync
- ✅ **Paint echo:** the host relays each stroke to every ready slot, including the author (`WorldReady.cs:47-51`). The author's filter (`Paint.cs:71`) checks the sender slot, which is now the host's, so the author's strokes are applied twice.
- ✅ **Turning map drawings from own back to shared duplicates every line** (`Markers.cs:44-58`). `_idOf` is cleared, then every placer marker is re-sent, including copies received from peers (`:164`, where they are restored into the same placer).
- **Any guest can hide any item** (`Items.cs:350-365`). In `Held`, `holder` comes from the payload. Fix: on the host, reject `holder != c.FromSlot`.
- **Checkpoints blocks `SaveCurrentMission` for the host as well** (`Sync/Checkpoints.cs:45-51`, UNSURE). If this is the campaign progress writer, a co-op host loses progress, and `__result=false` may trigger a game failure path. Confirm it only writes checkpoints.
- **With `TimerPercent == 0`, the prefix stops `CounterBatteryTimer.Init` from running** (`WorldEvents.cs:585-588`, UNSURE). Stale `_expired` and `_permanentlyStopped` values from the previous mission are then broadcast. Fix: let Init run and block only StartTimer.
- **PrinterAlerts:**
  - It scans every 0.25 s on the host in every phase, including menus and the map. One printer that throws aborts the pass (`PrinterAlerts.cs:39`, `:55`, `:61-72`).
  - A guest's local dismiss is never reconciled with the host (`:117-121`, UNSURE).
  - There is no `LocalReady` gate (`:87-111`), and Dismiss is accepted from any slot without a throttle.
- **SharedDice may not actually be shared** (`SharedDice.cs:65-78`, UNSURE). The seed wraps `ShellVisual.Update`, but the rotation may be rolled in Start or OnEnable instead. Confirm with ScenariosDice.
- **Animators don't subscribe to `HostRules.Changed`.** When SharedHover is turned off mid-session, guests keep the last hover pose.
- **Visibility** walks the whole mission scene every 4 s on the host (`Visibility.cs:53-89`).
- **Toggles:**
  - It runs 7 `FindObjectsOfType` calls every 5 s (`Toggles.cs:53-87`).
  - A respawned component at the same path is never re-tracked (`:80`, `:94`).
  - Guests can set the fire-control confirm buttons with no order check (`:130`, `:205`, UNSURE).
  - Three different "personal object" filters disagree: `Toggles.cs:100`, `Controls.cs:462`, `Animators.cs:29-33`.
- **Unlocks:** `RefreshAll` runs outside `Coop.Apply`, which may echo (`Unlocks.cs:56-63`). `DestroyWhenSceneObjectUnlocked` decisions made at scene start are never undone.
- **Resync answers:** `WorldReady.cs:56-69` has no ready-slot check, so each repeat re-snapshots every module. `WorldEntities.cs:264-266` has no epoch, ready or throttle check. `Gunnery.cs:189` checks the epoch, but answers any slot with no throttle.
- **Digest false positives** trigger full turret restores during normal lag (`Gunnery.cs:262`).
- **Peer floats aren't checked for NaN/infinity:** `Avatars.cs:257-258`, `Cats.cs:116-117`, `Controls.cs:275`, `:285-290`.
- **Peer-driven rescans in Controls** (`:277`, `:298`, `:516`): unknown ids trigger rescans, and `ButtonId` forces a full one.
- **Music host accepts any record id**, including locked ones, without checking the slot is empty (`Music.cs:116-124`).
- **Late-join gaps:**
  - The Espresso snapshot omits can uses (`:73-77`).
  - ScoutPlane is not in `WorldEvents.SendSnapshot` (`:121-160`).
  - The `ItemId.cs:13` TODO is still open.
- **`IntPtr` keys can be reused (ABA):** `Items.cs:31-54`, `ItemId.cs:50`, `:120` (`SpawnedOf` is never cleared), `Markers.cs:107`.
- **Avatars:** a Material is created per part and per pip and never destroyed (`Avatars.cs:394`, `:346`, `:425-434`). Avatars and cats have no world-ready gating.
- **Cat names go into TMP rich text unsanitized** (`PlayerInfo.cs:45` to `Cats.cs:184`; `Avatars.cs:320`, `:325`, `:357`). Steam friend names go into IMGUI labels unstripped (`CoopUI.cs:204`, `:266`).
- **Per-frame allocations:**
  - `Gunnery.cs:101`, `:107`
  - `Items.cs:210`
  - `Animators.cs:140`
  - `CoopUI.cs:158` (`Enum.GetValues` plus LINQ), `:284`, `:416`, `:425`
  - `PlayerHud.cs:15`
  - name tags in `Cats.cs:182-184` and `Avatars.cs:311-325`
- **Fake-null `?.` on Unity objects:**
  - Gunnery: `:61`, `:66`, `:152`
  - Items: `:268`, `:271`, `:449`
  - WorldEvents: `:349`
  - Requisition: `:47`, `:71`
  - Cats: `:43-44`, `:176`
  - ItemId: `:92`, `:106`
  - Toggles: `:166-167`
- **Also still open:** CounterBattery and MarkMissionFailed are not gated (`GraphGate.cs:30-50`); local-only units are never removed (`WorldEntities.cs:285-287`); restart may load twice (`MissionFlow.cs:169-188`, UNSURE).

---

## 4. Low / cleanup

### Networking
- `Protocol.cs:13`: the "session nonce" comment is still wrong.
- `NetReader.cs:45-55`: VarU still accepts overflowing and non-canonical varints.
- `NetReader.cs:33`: use `n > _end - _pos` to avoid overflow.
- `IronNestCoop.Net.csproj:7`: `AllowUnsafeBlocks` is set but unused.
- `Session.cs:170-173`: Dispose doesn't dispose the transport. Coop does it on Closed.
- `Session.cs:498`: handler-exception logs are not rate-limited.
- `Session.cs:238-239`: Expire and TickHost/TickGuest run outside the try.
- `TcpTransport.cs:138`: the frame delivery byte is not range-checked.
- `Session.cs:441-461`: a bad roster is half-applied before validation fails.
- `Session.cs:337`: a throwing `AdmitFilter` leaves the joiner hanging for 20 s.
- `Fragmenter.cs:39`, `:78`: messages over 16 MB are dropped silently with no log.
- `Fragmenter.cs:46-48`: `BitConverter` endianness.
- `Session.cs:507`: bidi and zero-width characters survive `Sanitize`.
- `Session.cs:109`, `:119`: Session is single-use.
- Log injection: CR/LF in peer `ver` and Bye reasons reaches the log (`Session.cs:293`, `:327`, `:335`, `:349`, `:417`).

### Core / Steam / UI
- `SteamApi.cs:53` doesn't null-check NetUtils, Friends or Utils.
- Config IDs 9/47/49 are hard-coded (`SteamApi.cs:57-60`, UNSURE).
- The lobby code is rebuilt with a fixed `0x0186` prefix (`SteamLobby.cs:49`, UNSURE).
- The PollCall fallback can't succeed (`SteamLobby.cs:174-177`).
- `_CurrentPhase_k__BackingField` is read at `CoopRunner.cs:105`, `Modules.cs:45`, `DevTools.cs:114`.
- `Coop.cs:154` still falls back to `Environment.UserName`.
- `Settings.cs:27` has no ranges; a negative Color throws in Bind (`:32`) after `Modules._done` is already set.
- `CoopRunner.cs:30` never restores `runInBackground`; `CoopLog`/`CoopRunner.cs:71`, `:78` key `ErrorOnce` on the message text.
- `Modules.cs:10-11` sets `_done` before creating modules, with no try; the comment at `:3` is stale.
- `LaunchArgs.cs:51-58` throws inside `Plugin.Load`.
- `HostRules.cs:76` calls Notify during teardown.
- `CoopUI.cs:209` allows 20 characters for an 8-character code.
- `Layout.cs:41-46`: `L.Btn` ignores `GUI.enabled`, so the disabled "Host lobby" still fires.
- `Layout.cs:35-38`: `Clicked()` ignores the scroll clip.
- `CursorRelease.cs:48-56` never releases if Release throws.
- `Theme.cs:86-90` can rebuild textures and leak the old set (UNSURE).
- `SkipIntro`: on by default and undocumented; it calls `FindObjectsOfType` every 0.1 s for up to 180 s (`SkipIntro.cs:20-27`).
- Unicode glyphs may render as boxes (UNSURE); UiProbe doesn't cover `GUI.Box(Rect,GUIContent,GUIStyle)`.

### Sync
- Unchecked `(int)r.VarU()` casts and unchecked enum ranges:
  - WorldEntities: `:98`, `:254`, `:268`, `:276`, `:304`
  - WorldEvents: `:174`, `:207`, `:358`
  - Gunnery: `:218`, `:255`
- `Gunnery.cs:319`, `:361`: GunIndex -1 becomes 255.
- `Gunnery.cs:88-89`, `:155`: `guns[i]` isn't null-checked.
- `Gunnery.cs:345-354`: the Finalizer restores `Applying` only if GunIndex recomputes to the same gun (UNSURE). Save the old value in the Prefix instead.
- `MissionFlow.cs:156`: Epoch is set before validation.
- `Resources.FindObjectsOfTypeAll` on a miss: `WorldEvents.cs:710`, `MissionFlow.cs:213`, `:231`.
- `Items.cs:190`: `Surfaces()` scan per placement.
- `Items.cs:155` vs `:274`: snapshot and poll disagree on includeInactive, and deactivation is never sent.
- `WorldEntities.cs:192-207`: digest every second even with no guests.
- `Avatars.cs:160`: the console path send has no length limit; the receiver allows 256.
- `Espresso.cs:106-111`: any slot can empty any cup.
- `Requisition.cs:172`: static `_playerDropped`.
- `Controls.cs:116-124`, `:173`: duplicate-id dials are re-added.
- `Requisition.cs:294`: wrong "spent" log label.
- `Music.cs:16`: stale comment.
- `Paint.cs:14-18`: the comment says the brush is per dab, but it is per batch.
- `Animators.cs:29-33`: substring filters exclude any path containing "Cat", "Camera" or "Fade".
- `NoPause.cs:138-142`: overrides game changes to `fixedDeltaTime` (UNSURE).
- `Visibility.cs:105-110` vs `:133`: no send cap; the receiver drops anything past 4096.
- `Util/Listeners.cs:23-46`:
  - Bare catches make `ViewOnly`/`ClearsMap` return false silently.
  - Reflection runs per call.
  - Only persistent listeners are seen.
- `Controls.cs:364-376`: listener toggling happens outside try/finally.
- `GameJson.cs:13-20`: no try (now low, since Session catches per handler); TypeNameHandling UNSURE.
- `ScenePath.cs:49-57`: `/` and `[` in names break lookups; scenes are resolved by name.
- `ItemId.cs:106-108`: O(n²); full scans are throttled globally, with no per-id miss cache.

### Duplication / dead code / hygiene
- Duplicated code: `SceneSignature` ×4 (`Animators.cs:66`, `Visibility.cs:44`, `Toggles.cs:42`, `ItemId.cs:26`); `Begin()` in Gunnery, Items, WorldEvents and others; three "personal object" filters.
- Dead code:
  - `LaunchArgs.IsTest`, `BridgePort`
  - `Msg.MapMarkerRemove`
  - `Coop.Started`/`Ended`
  - `SteamLobby.SetData`/`SetJoinable`
  - `Layout._viewTop`/`_viewBottom`
- Misc: `MissionStatus` is misplaced in `Modules.cs:39`; the `LaunchArgs.Sandbox` field shares its name with the `Sandbox` class.
- Test hooks in shipping code:
  - `Avatars.cs:95`, `:121`
  - `Music.cs:55`
  - `Espresso.cs:130`
  - `Cats.cs:70-71`
  - `Controls.cs:208`, `:477`
  - `Requisition.cs:45`
- Build:
  - `Directory.Build.props:4`: `GameDir` defaults to `F:\IronNestCoopDev\game`.
  - The version is duplicated (`Directory.Build.props:8`, `Plugin.cs:14`).
  - Both shipped DLLs embed the local PDB path; add `PathMap`/`Deterministic`.
  - CS0618 is suppressed.
- `.gitignore`: still missing `__pycache__/` and `*.pyc`, and `tools/__pycache__/coopqa.cpython-311.pyc` still exists. Fix this before the first commit.
- `docs/` is empty, so git won't track it.
- `tools/package.py` runs no release checks (placeholder text, "(test build)" label, PDB path).

### Release docs and provenance
- `dist/README.txt:1` still says "(test build)", and `:7` still has the `<game company>\<game>` placeholder.
- README.md has no install, host or join section (`dist/README.txt:6-13` has them).
- No credits for BepInEx, Harmony or Il2CppInterop.
- No warning about the direct-connect exposure.
- SkipIntro is undocumented.
- The "coop-anything playbook" is still cited without attribution (`QA/Bot.cs:12`, `SteamApi.cs:10-11`, `tools/coopqa.py:6`).
- The plugin name and GUID `IronNestCoop` still collide with Xeltor's All-Rights-Reserved mod.
- Repo search found only the `ItemId.cs:13` TODO; no `C:\Users` paths, usernames, Steam IDs, `Debug.Log` or `#if DEBUG`.

---

## 5. Test coverage gaps (`tests/IronNestCoop.Net.Tests`, still 20 tests)

None of the fixes since v1 has a test. Missing:
- TcpTransport tests: greeting deadline, duplicate-id refusal, frame limit, connection flood, Dispose.
- ChaosTransport tests.
- Fragmenter tests: nested-fragment rejection (B1), duplicates, the 8-partial cap, `Expire`, the 16 MB cap.
- A handler or subscriber that throws inside Tick (B4).
- Out-of-range Welcome slot, NaN Pong, oversized Info and names, `ClampInfo`, `Sanitize` with surrogate pairs.
- The fuzzer still generates kinds 1-10 only (`Program.cs:344`) and never builds a valid nested fragment.

---

## 6. Already good
- **Release zip** (`dist/out/IronNestCoop-0.1.0.zip`):
  - 4 entries: `BepInEx/plugins/IronNestCoop/{IronNestCoop.dll 727,040 B, IronNestCoop.Net.dll 40,448 B}`, `README.txt`, `LICENSE`.
  - Hashes match the current Release build.
  - No PDBs, test DLLs, interop DLLs or game files.
  - The version is consistent everywhere.
- **Host rules:** spoof-proof and read-only for guests, which matches the "ambiguous behaviour becomes a host toggle" design.
- **DevTools:** host-only, off by default, and nothing on the network can reach it.
- **Music:** echo handling is sound (the Place and Remove prefixes gate on `Suppress`), and position correction now has a valid host clock.
- **Cats:** there is still exactly one puppet per player slot, and cleanup on leave works.
- **Items:** the session reset is now complete.
- **PrinterAlerts:** the host sends only to ready slots, and the echo is guarded.
- **Wire primitives and Steam message offsets** remain correct.

---

## 7. Suggested fix order
1. **First commit:** add `__pycache__/` and `*.pyc` to `.gitignore`, then make the first commit so every fix can be reviewed.
2. **Session lifecycle (B6):** add `OnSessionStart`/`OnSessionEnd` resets to Gunnery, WorldEvents, MissionFlow, WorldEntities, SharedDice, Requisition and Music. Make epoch keys session-unique. Gate guest Music, Espresso and Machinery on `WorldReady.LocalReady` and reset them per mission.
3. **Direct-connect (B2):** remove it, or lock it down with a bind address, passphrase, connection cap, Hello deadline and async connect.
4. **QA gating:** put `QA/`, `Dev/`, the mutants, nopatch and the test hooks behind `#if COOP_QA`. Install the Sandbox patches only in the sandbox.
5. **Trust and robustness:**
   - Clamp and rate-limit Paint, and fix the Paint echo.
   - Fix the Animators rescan and parameter misalignment.
   - Add epoch and ready checks to Controls presses.
   - Validate the Items `Held` holder.
   - Check peer floats for NaN/infinity.
   - Destroy failed cat and avatar builds on catch.
   - Wrap every Harmony body in try/catch.
   - Cache "not found" lookups.
6. **UI and Steam flow:**
   - Confirm before a join request makes the host leave.
   - Treat a connecting guest as a guest.
   - Per-field Enter.
   - Fix Cancel and the lobby ordering.
   - Show errors as Notify toasts.
   - Confirm Ban, and add an unban UI.
7. **Network tests:** cover every defence listed in section 5.
8. **Docs:** placeholder, label, install/host/join, credits, exposure warning, SkipIntro, attribution, name check, PDB `PathMap`, `GameDir` default.
9. **Two-account Steam playtest (B7)**, focused on the UNSURE items:
   - Music start and track re-issue.
   - Cat hat values.
   - Animator catch-up on float-driven animators.
   - SharedDice photo rotation.
   - Checkpoints and campaign progress for the host.
   - Teleprinter alarm printer index.
   - Restart double-load.
   - Map-drawing rule flips.
   - A slider being dragged when someone joins.
   - Leaving a session and hosting or joining a second one in the same game launch (B6).

---

## 8. Fix status (2026-10-28, after the v2 audit)

Fixed in code; each still needs the two-account playtest before it counts as done in real play.

- **B2 direct connect:** join key in the TCP greeting (constant-time compare, wrong key answered after 1 s); a host
  without a key listens on loopback only (tests), with one on all interfaces; at most 16 connections and 4 still
  greeting; Hello due within 15 s of the greeting; ids 0 and the host's own refused; delivery byte checked;
  Thread.Start in try; Host and Join wrapped in try and reported through `Fail()`; Join connects on a worker task;
  no lasting Ban on direct sessions; the UI says it is for trusted networks only. 5 new TcpTransport tests.
- **B6 session lifecycle:** epoch base is fresh per session (host); OnSessionStart/End resets in Gunnery,
  WorldEvents, MissionFlow (`_pendingStart`), WorldEntities (`_announced`), Music, Espresso, Machinery, Controls
  (`HeldSliders`, replayed drags); Music/Espresso/Machinery guests follow only this epoch's host state, once
  LocalReady.
- **QA switches:** mutants, `-coop-nopatch` and `-coop-uiprobe` are inert unless started with `-coop-sandbox=`
  (only the QA runner passes it). QA/Dev types are still compiled in (see "Still open").
- **High:** Paint clamp + per-slot rate limit; Animators rescan throttle and parameter skip; Controls epoch/ready
  checks, SendToReady, rescan throttle, drag state kept across rescans, per-button registration; Cats/Avatars
  destroy failed builds, back off 10 s, validate the hat index; Harmony bodies wrapped in try/catch
  (`CoopLog.PatchFailed`, bool prefixes fail open to the game's own code); per-frame searches (Music, Cats,
  Espresso, Avatars `Table()`) throttled or negatively cached.
- **Medium:** per-field Enter; Items `Held` holder must be the sender; peer floats checked for NaN/infinity
  (Avatars, Cats, Controls); PrinterAlerts mission-only, try per printer, Dismiss from ready slots only and
  throttled, host answers each dismiss with its state; timer rule config snaps to the offered choices (only 0 is
  off); Toggles respawned components re-tracked; Toggles/Visibility/Animators scans spread out or cached (they were
  the 10-40 ms in-mission hitches).
- **Kept on purpose:** a guest pressing a button no scan can name still runs it locally (logged). Failing closed
  would make that button dead for the guest.

### Still open
*Updated by audit round 3 (2026-10-29); see section 9 for details.*

**Unchanged in round 3**
- QA/Dev code compiled into the release DLL. This needs a separate QA build configuration, and the tested build
  would then differ from the shipped one, so it is the owner's call.
  - New: `-coop-chaos` is still parsed and applied in normal play (`LaunchArgs.cs:62-63`, `Coop.cs:260-261`).
- Steam:
  - Cancel during create/join (the create/join callbacks still start a session).
  - Begin() lobby ordering.
  - Auto-init gives up after 90 s (`CoopRunner.cs:42`).
  - Reliable send sleeps (`SteamTransport.cs:39-51`).
- N-NET2/3/4: fragment budget per peer, PlayerInfo broadcast coalescing, slow reader. Round 3 adds R3-M10:
  completed fragment ids can be replayed.
- Replays through `Coop.Apply` instead of `ApplyVisual` (still in Espresso, Machinery, Toggles and the scout
  plane). Animator state catch-up limited to triggers (UNSURE).
- Teleprinter alarm printer mapping (UNSURE). Checkpoints still blocks the host's `SaveCurrentMission`
  (`Checkpoints.cs:57-60`, effect UNSURE).
- Remaining Medium/Low items in sections 3 and 4, docs (section 7.8), and tests for the other defences
  (section 5).
  - Tests are now 25; the 5 new ones are TCP tests.
  - Still untested: nested fragments, fragment caps/expiry, handler exceptions in Tick, the 16-connection cap,
    the first-frame deadline, ClampInfo, NaN Pong/Roster.

**Section 8 claims that round 3 found only partly done**
- B2:
  - The 16-connection cap is not checked when a greeted connection is added (`TcpTransport.cs:152`), so up to
    16 + 4 are possible.
  - "Hello within 15 s" is a per-read timeout on the first frame of any kind, not a Hello deadline. After it,
    reads wait forever.
  - The direct-session Ban is only hidden in the UI. `Bans.Ban`/`AdmitFilter` are not guarded, and Kick's
    rejoin block is keyed on the guest-chosen id.
  - Pre-auth greeting DoS: see R3-H3.
- Harmony bodies: not wrapped in `Requisition.cs:300-306`, `Controls.cs:549-559`, `Gunnery.cs:397-420`,
  `Markers.cs:235-239`, `WorldEvents.cs:836-840`. SharedDice's Finalizers restore the RNG, so that one is covered.
- "Bool prefixes fail open": on a *guest*, failing open in Espresso/Machinery runs the game's own simulation
  (R3-M7).
- Peer NaN checks: Items `Pose` was missed (R3-H5).
- Paint rate limit: can be bypassed with a negative count (R3-H1).
- Scans spread: scene-change branches and `ScanForUnknown` bypass `Perf.HeavyFrame` (R3-M16).
- Items `Held`: the holder must be the sender, but any guest can still claim any unheld item (R3-L).

**Not in section 8, still open**
- Requisition `_usesEpoch`/`_firstUses`/`_spent` are not reset in `OnSessionStart`. The fresh epoch base now
  makes the epoch-change reset work, but the guest's save can read stale `Spent` until the first deck message.
- Markers own→shared flip re-sends peers' copies (duplicate lines; `Markers.cs:47`, `:54`).
- The Espresso snapshot still omits can uses.

**Verified fixed in round 3**
- All B6 session resets, on host and guest.
- Fresh epoch base per session.
- Guest Music/Espresso/Machinery gated on epoch + LocalReady.
- Paint clamps and the echo to the author.
- Animators throttle and parameter skip.
- Cats/Avatars destroy-on-fail plus 10 s back-off.
- PrinterAlerts mission-only, Dismiss throttle and reply.
- Toggles re-track.
- TCP: constant-time key compare, 1 s wrong-key delay, ids 0/host refused, delivery byte, `Thread.Start` in try,
  Host/Join through `Fail()`, Join on a worker task (thread-safe).

---

## 9. Audit round 3 (2026-10-29)

| | |
|---|---|
| Method | Six read-only Claude Haiku auditors: presence; ItemId/Controls/NoPause; net/core claims plus tests; sync claims plus recent changes; a sweep of everything a remote peer can send; performance hot paths. The coordinator checked every HIGH and most MEDIUM claims against the source. |
| Source state | src/ was being edited by another session during the audit, so line numbers may drift by a few lines. |
| Build | `dotnet build` (Release) into a separate output folder: 0 warnings, 0 errors. |
| Tests | **PASS 25/25**: the original 20 plus 5 TcpTransport tests. |
| QA | coopqa was not run. |

**Markers:** ✅ = verified by the coordinator in code. **No marker** = auditor claim, not re-checked. **UNSURE** =
depends on game internals or needs a runtime check.

### 9.1 Verdict
Clear progress: session lifecycle (B6), the Paint/Animators/Cats fixes and the TCP join key all hold up in code.

No new BLOCKER. The top items are:
- **Peer-input gaps:** a negative count bypasses the Paint rate limit; DeckOrder has an uncapped loop; Items
  Pose accepts NaN; guest broadcasts are relayed before validation.
- **The ItemId name scheme is not deterministic across machines.**
- **Direct-connect:** pre-auth greeting DoS, and the join key is written to the log.
- **Presence:** no Discord pipe timeouts; status is on by default and publishes the lobby id even for
  invite-only lobbies. Both need an owner decision.

### 9.2 High

**R3-H1 ✅ Paint rate limit bypass (negative count)** (`Sync/Paint.cs:88`, `:96-98`)
- **Problem:** `int n = Math.Min((int)r.VarU(), 4000)`. A VarU of 0x80000000 casts to `int.MinValue`, and
  `Math.Min` keeps it.
  - The loop doesn't run.
  - `bud.Used + n` goes hugely negative and is stored, so the sender's next messages pass the 6,000-dab cap for
    the rest of that 1 s window and every later window that reuses the stored value.
  - 0-dab messages are never counted but are still forwarded to every ready slot.
- **Fix:** `ulong raw = r.VarU(); if (raw > 4000) return; int n = (int)raw;`, and also count messages, not just
  dabs.

**R3-H2 ✅ DeckOrder count uncapped** (`Sync/Requisition.cs:222-224`)
- **Problem:** `int n = (int)r.VarU(); for (i < n) want.Add(r.Str(256));` runs before the deck lookup.
- **Attack:** a lobby member sends n = 2³¹−1 followed by a ~16 MB fragmented message of zero bytes. Each empty
  `Str` costs one byte, so the host (and every guest the message is relayed to; see R3-M1) builds a ~16-million
  entry list. That's a multi-second hitch and about 128 MB.
- **Fix:** read as `ulong`, cap at about 256, and resolve the deck before reading the list.

**R3-H3 ✅ Direct connect: pre-auth greeting DoS** (`src/IronNestCoop.Net/Transport/TcpTransport.cs:118-143`)
- **Problem:** at most 4 connections may be greeting. The 5 s `HandshakeTimeoutMs` is a *per-Read* timeout, not a
  total deadline, and a wrong key also holds the slot for 1 s.
- **Attack:** four sockets that each trickle one byte every ~4 s (the greeting is 8 + 1 + key bytes) keep the pool
  full for about a minute each and can be reopened. Every real joiner is refused at accept.
- **Scope:** LAN or port-forwarded hosts only.
- **Fix:**
  - One total greeting deadline (a timer that disposes the socket).
  - A per-remote-IP cap of 1–2 greeting connections.
  - Optionally, a separate pool for peers that already sent a valid key.

**R3-H4 ✅ ItemId name ids depend on a count frozen at the first scan** (`Util/ItemId.cs:37-67`)
- **Problem:** `NameCount` is built only in `EnsureScanned` when the scene signature changes. `Register` then
  decides "unique name → `origin:<scene>//<name>`, else path id" from that frozen count.
- **Failure:** the count depends on *when* each machine scanned (spawner deliveries, printed cards,
  includeInactive objects, a late joiner's later scan), so host and guest can pick different id kinds for the
  same item.
  - Example: the guest scans while one "X" exists and uses `origin:S//X`. The host scanned when two existed and
    uses path ids.
  - The guest's Place/Pose then resolves to null on the host (`Items.cs:346` "unknown item"), and the item stays
    unsynced until the scene changes. Frequency UNSURE.
  - Later registrations that collide fall back to `path#n`, a suffix numbered in local `FindObjectsOfType` order
    (`:65`). No other machine is guaranteed to produce the same number.
- **Fix:** use name ids only for items authored in the scene (not spawned), decided from data that is the same on
  every machine. Or let the host send its id table to guests. Sort by ScenePath before numbering.

**R3-H5 ✅ Items `Pose` applies unchecked floats** (`Sync/Items.cs:334-338`)
- **Problem:** position and quaternion go straight into `SetPositionAndRotation` with no `float.IsFinite` check
  (Avatars, Cats and Controls got the check; Items was missed). The pose is also relayed to everyone.
- **Failure:** NaN/Inf moves the item to an invalid transform on every machine. Physics and renderer impact
  UNSURE.
- **Fix:** reject non-finite values and clamp the magnitude (as in `Controls.cs:368`).

### 9.3 Medium: security (remote peer)
- **R3-M1 ✅ Guest broadcasts are relayed before validation, with no rate limit**
  (`src/IronNestCoop.Net/Session/Session.cs:480-489`).
  - The host forwards a guest's `TargetAll` packet to every other guest *before* `Dispatch` checks the type,
    epoch or handler. One malicious guest's traffic is multiplied by (players − 1).
  - Every guest also parses it (for example, 1 MB marker or item JSON).
  - Fix: dispatch first, then relay only messages the handler accepted (or keep a per-type relay allowlist), and
    add a per-slot message/byte budget.
- **R3-M2 ✅ The direct-connect join key is written to the log.**
  - `LaunchArgs.Describe()` is `string.Join(" ", Seen)`, and `Seen` contains `-coop-key=<key>` (`LaunchArgs.cs:57`,
    `:73`, `:86`). `Plugin.cs:39` logs it at load.
  - Logs get attached to bug reports.
  - Fix: redact the value in `Describe()`.
- **R3-M3: the join key is sent in cleartext and frames are not authenticated**
  (`TcpTransport.cs:83-87`, `:144`; `Session.cs:316` trusts the transport's peer).
  - Anyone who can sniff the LAN learns the key and can inject frames.
  - Acceptable only with the existing "trusted networks only" label. Otherwise use a keyed challenge-response
    plus a per-frame MAC, or TLS.
  - For scale: guessing the key is not realistic (~30-bit key from a CSPRNG, at most about 4 guesses per second).
- **R3-M4: the host checks its own readiness, not the sender's**
  (`Controls.cs:359`, `:386-398`; same pattern in `Paint.cs:80-82`, `Items.cs:326`, `Espresso.cs:200`,
  `Markers.cs:152`, `Toggles.cs:220`, `Gunnery.cs:193-199`).
  - The test `WorldReady.LocalReady || IsHost` is true on the host for any sender. A guest that knows the epoch
    (MissionStart is broadcast) but hasn't loaded can press any registered host button.
  - Fix: on the host, require the sender to be in `WorldReady.Slots`, as PrinterAlerts already does.
- **R3-M5: Items `Place` and Markers `Add` parse up to 1 MB of JSON per message, unthrottled.**
  - `Items.cs:344-357` also runs a `Surfaces()` FindObjectsOfType per message.
  - `Markers.cs:156-176` parses JSON before the placer null check, has no per-slot or total marker cap, and lets
    any slot remove any marker.
  - Fix: smaller string caps (a placement is a few hundred bytes), check the target before parsing, rate-limit per
    slot, cache `Surfaces()` per scene, and record marker owners.
- **R3-M6: Toggles `Dismiss`** (`Toggles.cs:253-260`) has no ready check or throttle, and is relayed 1→N. Fix:
  reuse PrinterAlerts' ready check and throttle.
- **R3-M10: completed fragment ids can be replayed** (`Fragmenter.cs:81`). The completed id is forgotten, so
  re-sent pieces rebuild and redeliver the message. Per peer, 8 × 16 MB partials are still possible (N-NET2). Fix:
  remember the last completed id per peer and keep a per-peer byte budget.
- **R3-M11: unbounded work per Tick.** The TCP inbox is drained fully (`TcpTransport.cs:226-234`; up to 64 MB
  queued per connection), Steam receive loops without a byte cap (`SteamTransport.cs:68-90`), and Session handles
  all of it in one frame. Fix: a per-Tick packet and byte budget.
- **R3-M12: ItemId punchcard misses** (`ItemId.cs:137-142`). Each unknown `punchcard:` id runs a full
  `GetAllCards` search, up to once per frame. Fix: cache misses for about 1 s, as save ids already are (`:168`).
- `Session.cs:496-498`: handler exceptions log a full stack trace per bad packet, not rate-limited (still open
  from round 2). Use `EmitLimited`.

### 9.4 Medium: presence and privacy (new code)
- **R3-M13 ✅ Discord pipe I/O has no timeouts** (`Steam/DiscordPresence.cs:176-233`).
  - It uses `PipeOptions.None` with synchronous Write/Read. A local process that creates `discord-ipc-0` before
    Discord, accepts, and never replies blocks the worker's handshake Read forever. The status then never
    recovers until restart.
  - The game thread is not blocked, and quit is bounded (`Join(1500)`, background thread).
  - Fix: open the pipe asynchronously, wait on ReadAsync with a timeout, and dispose on timeout.
- **R3-M14: Discord pipe squatting discloses status.** `Connect` takes the first `discord-ipc-N` that answers. A
  squatter receives the handshake and every SET_ACTIVITY (mission, station, party id with the lobby id, pid). That
  is local disclosure only: nothing from the reply is executed. Fix: check `GetNamedPipeServerProcessId` belongs
  to Discord (or Discord PTB/Canary).
- **R3-M15 ✅ Presence is on by default and publishes the lobby id whatever the lobby's visibility.**
  - `Settings.cs:21` and `:23` default Steam and Discord status to **on**.
  - `SteamPresence.cs:31-35` sets `steam_player_group` to the lobby id, and the Discord party id is
    `nestmates-<lobby id>` (`DiscordPresence.cs:83`), even when the host chose **Invite only**.
  - Steam should still refuse a non-invited JoinLobby (UNSURE; check with a second account). The Discord party id
    is new exposure to people who aren't Steam friends.
  - The `[Discord] ShowStatus` description doesn't mention that the party/lobby id is shared.
  - Fix:
    - Omit the group/party for invite-only lobbies.
    - Spell out in the descriptions what is shared.
    - **Owner decision:** keep default-on or make it opt-in.
- **Discord reconnect has no back-off** ✅. `Update` calls `Wake.Set()` every 2 s, which cuts the failure-path
  `WaitOne(15000)` short. While Discord is closed, or an app id is refused, it retries every 2 s (up to 10 pipe
  opens of 200 ms each, or a full handshake). Fix: signal only when the wanted activity or app id changes, and
  back off from 15 to 60 s.

### 9.5 Medium: correctness
- **R3-M7 ✅ Guest Update prefixes fail open into local simulation** (`Sync/Espresso.cs:286-292`,
  `Machinery.cs:155-163`). On a guest, an exception in `ApplyHost` returns `true`, so the game's own brew or engine
  simulation runs locally. A per-frame exception means permanent local simulation and desync. Fix: on a guest,
  return `false` from the catch and log once.
- **R3-M8 ✅ Music leaves hidden records hidden after the session** (`Sync/Music.cs:43-44`, `:161-168`).
  `OnSessionEnd` calls `OnSessionStart`, which clears `_hiddenRecords` without `SetActive(true)`, so records a
  guest hid stay hidden in single-player. Fix: re-activate the tracked records in OnSessionEnd, then clear.
- **R3-M9 ✅ Controls replay flag can stick** (`Sync/Controls.cs:316-317`). In Tick, `!Synced(e) … continue` runs
  before the replay-timeout check.
  - If the shared-record-volume rule turns off mid-replay, `ReplayingDrag` stays true. The local hand's values are
    then not sent, and remote values can override it until a scene change or session end.
  - The static `_replayingDrag` itself is in try/finally and cannot stick. A nested replay resets it to `false`
    instead of restoring the previous value (`:239`).
  - Fix: run the timeout check before the `Synced` check, and save/restore the static.
- **Controls `HeldSliders` keeps the pointer of a slider destroyed mid-replay** (`Controls.cs:224`, `:230`,
  `:673`). Rescan doesn't prune it. A new slider at a reused address then has its Update suppressed for the rest
  of the session (UNSURE how often addresses are reused). Fix: prune in Rescan, or end the replay when its entry
  dies.
- **NoPause forces `timeScale` back to 1 every session frame when it is 0** ✅ (`Sync/NoPause.cs:167`). This
  undoes any game freeze that isn't the pause menu (cutscene, dialog, loader). UNSURE whether the game has any.
  Scene load with a menu open is also UNSURE. Fix: restore only a timeScale the pause menu set.
- **Coop.Apply replays still lift GraphGate:** Espresso (`:215`, `:246`, `:261`, followed by the effect replay at
  `:264-265`, so effects may play twice), Machinery (`:136-146`), Toggles (`:258`), and the scout plane
  instantiate/register (`WorldEvents.cs:438-453`). UNSURE which have mission-graph listeners. Fix: use
  `ApplyVisual` for presentation-only calls.
- **The scout dedupe key `nodeId@x;z`** (`WorldEvents.cs:436-437`) drops a second legitimate flight from the same
  node and spot (UNSURE). Fix: a host-assigned sequence number.
- **ItemId pointer-keyed caches return a dead object's id** (`ItemId.cs:59-61`, `:152`). `OriginOf` and
  `SpawnedOf` aren't checked for liveness, so an object recreated at the same address inherits the old id and
  `Find` returns null. Fix: store the GameObject with the id and re-register when it differs.

### 9.6 Medium: performance

| # | Where | Runs | Cost | Fix |
|---|---|---|---|---|
| R3-M16 ✅ | Scene-change branches bypass `Perf.HeavyFrame`: `Visibility.cs:61`, `Animators.cs:79`, `Toggles.cs:63-66` (all 7 searches at once), `Controls.ScanForUnknown`/`Rescan(true)`, plus scans that never claim the slot (Music `Player`/`SendRecords`, Cats, Espresso, Machinery, PrinterAlerts, `Avatars.Table/Local`, SkipIntro, Gunnery digest) | both | Several scans land in the scene-load frame, which is the 10–40 ms hitch class `Perf.cs` targets | Route the scene-change path through HeavyFrame and defer by a frame |
| | `Visibility.cs:58-119` | host | Full `GetComponentsInChildren<Transform>` walk every 4 s (`_seen` skips only the path build), plus a 4 Hz poll over every watched transform (UNSURE: scene size) | Walk on scene change only; watch the known switchable types |
| | `Animators.cs:136-172` | host, 20 Hz | Polls about 290 entries (params, IsInTransition, state info) and allocates a list per poll; `IsPersonal` builds a LINQ delegate per path | 10 Hz, sample changed entries, cache IsPersonal |
| | `Controls.Rescan(true)` with `Util/Listeners.cs` | both, per snapshot or peer-triggered | 4 FindObjectsOfType, a ScenePath per control, reflection per LookAtTarget | Add only missing ids; cache Listeners per button |
| | Harmony prefixes on per-frame game methods (`Items.cs:469-470` HeldUpdate for every DraggableItem; Controls, Espresso, Machinery, Gunnery, SharedDice, GraphGate) | both, **even outside a session** | An interop dispatch per instance per frame (UNSURE: count) | Drop HeldUpdate and pin remote-held items from `Items.Tick` |
| ✅ | `Core/Coop.cs:318` `Safe(m, () => m.Tick(dt))` | both, every frame | Closure allocation per module per frame (about 23) | Inline the try/catch |
| ✅ | `Items.cs:226` `new List<IntPtr>(RemoteHeld.Keys)` | both, every frame | Allocation even when empty | Skip when Count == 0; reuse a buffer |
| | `Music.cs:245-251` → `ItemId.Of` | guest, every frame while a record is on | About 8 GetComponent calls | Cache per RecordItem pointer |
| | `Avatars.cs:313-347`, `Cats.cs:249-268` | per remote, every frame | PlayerInfo.Decode, string building, TMP `.text` set | Recompute on change |
| | `Avatars.cs:154-192` | sender, 20 Hz at a console | ScenePath per zone and raycast hit; the ~100-char path is sent in every pose | Cache by pointer; send a station index |
| ✅ | `UI/SkipIntro.cs:18-24` | 10 Hz for the first 180 s | FindObjectsOfType every 100 ms, not HeavyFrame-gated | Search once per scene signature |
| | `CoopRunner.cs:90`, `UI/CoopUI.cs:94`, `:156`, `PlayerHud.cs:13-18` | each OnGUI | String per call, Enum.GetValues + LINQ, Encode+Decode per player | Constant names; compute HUD text once per Tick |
| | `Espresso.cs:255-274` | guest, every frame | ApplyHost closure plus game gauge writes every frame | Apply on change |
| | `Resources.FindObjectsOfTypeAll` (`WorldEvents.cs:148-158`, `:927`; `Requisition.cs:84`; `MissionFlow.cs:237`, `:255`) | per event | Searches the whole asset set | Cache per mission |
| | `Settings.cs:19` Verbose defaults to **true** | always | Every Debug line is formatted and written with AutoFlush | Default false for release |

Bandwidth, roughly: 1–3.5 KB/s up per player, about 5 KB/s while dragging. Host egress is about (players − 1) × each
guest's upstream because of the relay. AvatarState (20 Hz), Cats (10 Hz), Gunnery TurretState (10 Hz) and
EntityDigest (1 Hz) are sent without change detection. Controls sends the full scene path as the id at 20 Hz while
dragging, so a short numeric id table would cut that.

### 9.7 Low
- **Presence:**
  - Discord reply text (up to 64 KB, raw newlines) is logged verbatim (`DiscordPresence.cs:154-155`, `:164`,
    `:190`), so a squatter can forge log lines.
  - Replies aren't matched to the nonce, so an unsolicited frame is read as the SET_ACTIVITY reply.
  - Truncation can split surrogate pairs (`DiscordPresence.cs:94`, `SteamPresence.cs:59`), and Steam may limit by
    bytes (UNSURE).
  - `Text` is set before the sandbox check, so the UI says "friends see" in test copies.
  - Every TCP game shares the party id `nestmates-direct`.
  - Dead code: `SteamPresence.Describe()` (no-arg) and `SteamLobby.SetJoinable`.
  - `PresenceInfo.Now()` runs every 2 s even with both toggles off.
- **Network:**
  - Greeting counter drift under races (`TcpTransport.cs:118-120`).
  - A first frame that isn't Hello keeps the connection open forever.
  - A wrong key looks like a 20 s timeout to the guest (`Session.cs:246`).
  - Host key not upper-cased while the guest's is (`TcpTransport.cs:58-62` vs `Coop.cs:203`).
  - The accept thread start isn't in a try (`:105`).
  - Dispose vs TryAdd race (`:152`, `:243-248`).
  - Leaks on failure: listener (`Coop.cs:171-173`) and connected transport (`:221`).
  - Fragmenter send allows about 2 GB while receive caps at 16 MB, so oversized messages are dropped silently.
  - Epoch base uniqueness is time-derived (`MissionFlow.cs:79`); negligible.
- **Startup and bans:**
  - Numeric launch args throw out of `Plugin.Load` (`LaunchArgs.cs:55`, `:58`, `:61`, `:63`).
  - `OldName.cs:25`, `:33` copy the old ban file whenever the new one is missing, which brings deleted bans back.
  - `Bans.cs:38` writes the raw name; `:18`, `:28` cache an empty set after a failed read.
  - `CoopUI.cs:270-274` does synchronous DNS on the game thread once.
- **Sync:**
  - Paint budget keyed on `c.FromSlot`: relayed paint arrives from the host's slot, so all painters share one
    bucket on guests (`Paint.cs:97-98`). Key it on `painter`.
  - Espresso:
    - `OnSessionStart` doesn't restore cups that were mid-drink.
    - `_wasFull` is never pruned, and static `_emptiedRemotely` is never cleared.
    - Host temp, pressure, quality and BrewState aren't range-checked.
  - The cat blend-shape index isn't checked against `blendShapeCount` (`Cats.cs:146-153`); it relies on the try.
  - `ImpactMarkerManager.Instance?.` fake-null (`WorldEvents.cs:452`).
  - Any guest can claim any unheld item (`Items.cs:380-385`), tick any fire-mission card (`:391-396`, intent
    UNSURE), take the host's record off (`Music.cs:183-194`, intent UNSURE), or toggle host auto-reload with a
    FindObjectOfType per message (`Gunnery.cs:203`, `:281`).
  - Avatars and Cats have no sender-ready gate and accept huge (finite) positions.
  - NoPause `OnSessionEnd` doesn't reset PauseManager's own paused flag (UNSURE).
- **ItemId:**
  - Culture-sensitive `StartsWith` (`:161`, `:167`).
  - The `GetComponentInChildren` fallback in `Find` lets an id target a draggable child of any save-identified
    object (UNSURE).
  - The `EnsureScanned` full scan isn't HeavyFrame-gated.
  - `ScenePath` `[n]` suffixes can collide with names that contain `[1]` or `/`.

### 9.8 Checked and OK in round 3
- **DiscordPresence:**
  - JSON escaping is correct (quotes, backslashes, control characters; lone surrogates are replaced by UTF-8).
  - The config app id can't inject: it must be 15–22 digits and parse as `ulong`.
  - The frame length check rejects negative values and anything over 64 KB, and partial reads are handled.
  - No pipe handle leaks.
  - No Unity or Il2Cpp object is touched off the main thread; `CoopLog`'s writer is lock-protected.
  - Discord is sent on change or every 20 s, within its rate limit. Steam calls are change-only.
- **Clearing status:** clears on quit, on disable, after leaving a session (next tick), and is never set in the
  sandbox.
- **Controls drag-lock patches:** the patched game methods are `void`, so a bool prefix without `__result` is
  correct. They fail open, and the static flag is only set inside the synchronous `Coop.Apply`, so the local
  player's own drag is unaffected.
- **NoPause:** settle and resume logic holds:
  - Closing a menu inside the settle window leaves nothing stuck.
  - The host can always unpause.
  - A player leaving while paused is handled.
  - Pause toggles are limited to about 2 broadcasts per second per guest.
- **Wire safety:** no `TypeNameHandling` or `DefaultSettings` anywhere, so peer JSON has no type gadgets.
- **Network tests:** the TCP key tests pass.

### 9.9 Suggested order for round-3 fixes
1. **Peer input** (small, mechanical):
   - R3-H1 Paint `ulong` count, plus message counting and keying on `painter`.
   - R3-H2 DeckOrder cap.
   - R3-H5 Items Pose finite check.
   - Read every `(int)r.VarU()` count as `ulong` with a cap before the cast; grep finds the rest.
2. **Relay after validation plus per-slot budgets** (R3-M1, R3-M11). Readiness checks on the sender (R3-M4).
   Smaller JSON caps and Markers ownership (R3-M5).
3. **R3-H4 ItemId:** make the id kind deterministic (scene-authored items only, or a host id table). Check it in
   the two-account playtest with differently arranged bases and a late joiner.
4. **Direct connect:** total greeting deadline plus per-IP cap (R3-H3); redact the key in logs (R3-M2).
5. **Presence:**
   - Pipe timeouts and a process check (R3-M13/M14); reconnect back-off.
   - Omit the lobby id for invite-only lobbies.
   - **Owner decision** on default-on status (R3-M15).
6. **Correctness:** Espresso/Machinery fail closed on guests (R3-M7), Music hidden records (R3-M8), Controls
   replay flag order (R3-M9), Requisition `OnSessionStart`.
7. **Performance:**
   - Scene-change scans through HeavyFrame (R3-M16).
   - Visibility walk.
   - Closure and list allocations per frame.
   - Verbose off by default.
   - SkipIntro search once.

### 9.10 Fix status (2026-10-29, after audit round 3)
Fixed (built, foldcheck PASS, network tests 29/29; in-game QA still to run):
- R3-H1 Paint: counts via `NetReader.Count` (unsigned, capped, malformed when over); budget keyed on the painter, each message costs 20 dabs.
- R3-H2 and every other count from the wire: `NetReader.Count(max, what)` in Animators, Music, Toggles, Visibility, Unlocks, Requisition (256), WorldEntities, WorldEvents, Paint. No `(int)r.VarU()` counts left (epoch compares only).
- R3-H3 TCP greeting: one 5 s deadline for the whole greeting (timer), at most 2 greetings per address, the 16-connection cap checked again at promotion, a real first-frame deadline (timer, 15 s) after promotion. Tests: trickled greeting cut, per-address cap.
- R3-H4 ItemId: name ids only for scene-authored items (no "(Clone)"); an unknown origin id (name or path form) falls back to the one item of that scene with that name (cached per scan).
- R3-H5 Items: Pose and Place reject non-finite or far-away values (positions < 1e5, non-zero quaternions, every field of the save state).
- Relay: per guest at most 2000 packets / 8 MB a second relayed or handled by the host (session clock); test added.
- `-coop-key` hidden in the log; `-coop-chaos` only in the sandbox.
- Espresso/Machinery Update prefixes fail closed on guests.
- Music OnSessionEnd shows the records it hid again.
- Controls: a replayed drag ends when its control stops being synced.
- Discord: every pipe read/write has a 5 s deadline (async pipe), the worker is woken only on a change, a real 15 s back-off, replies logged shortened with user data cut.
- Presence: Steam group key and Discord party id are a hash of the lobby id, not the id.
- Harmony bodies with out parameters wrapped: Requisition GuestCardUses, Controls Hover, Gunnery ShellInitPatch (both), Markers Erased, WorldEvents PrefabSpawnPatch.

Left open (reasoned):
- Presence on by default: owner decided 2026-10-29 to keep it on (easy to turn off; the station part stays off).
- Host readiness checks use the host's own readiness: messages carry the epoch, and a guest only sends from inside the mission; low risk, not changed.
- NoPause forces time scale 1 in a session: intended (a stopped copy would fall out of the shared world).
- Scene-change scans outside HeavyFrame: one hitch while a scene loads anyway; not changed.
- Direct-session ban: ids are chosen by the guest there, so a ban can't bind; the UI hides it, the key is the gate.
