using System.Collections.Generic;
using IronNestCoop.Core;

namespace IronNestCoop.QA
{
    /// <summary>
    /// QA scenarios. Each returns the steps for this machine's role; host and guest halves of one
    /// scenario coordinate through bot signals. Compose waits with
    /// <c>foreach (var x in bot.Until(...)) yield return x;</c>.
    /// </summary>
    public static partial class Scenarios
    {
        public static IEnumerable<object> Create(Bot bot) => bot.Scenario switch
        {
            "connect" => bot.IsHostRole ? ConnectHost(bot) : ConnectGuest(bot),
            "mission" => bot.IsHostRole ? MissionHost(bot) : MissionGuest(bot),
            "entities" => bot.IsHostRole ? EntitiesHost(bot) : EntitiesGuest(bot),
            "world" => bot.IsHostRole ? WorldHost(bot) : WorldGuest(bot),
            "solo" => SoloMission(bot),
            "avatars" => bot.IsHostRole ? AvatarsHost(bot) : AvatarsGuest(bot),
            "controls" => bot.IsHostRole ? ControlsHost(bot) : ControlsGuest(bot),
            "gunnery" => bot.IsHostRole ? GunneryHost(bot) : GunneryGuest(bot),
            "engine" => bot.IsHostRole ? EngineHost(bot) : EngineGuest(bot),
            "music" => bot.IsHostRole ? MusicHost(bot) : MusicGuest(bot),
            "cats" => bot.IsHostRole ? CatsHost(bot) : CatsGuest(bot),
            "items" => bot.IsHostRole ? ItemsHost(bot) : ItemsGuest(bot),
            "markers" => bot.IsHostRole ? MarkersHost(bot) : MarkersGuest(bot),
            "unlocks" => bot.IsHostRole ? UnlocksHost(bot) : UnlocksGuest(bot),
            "pause" => bot.IsHostRole ? PauseHost(bot) : PauseGuest(bot),
            "latejoin" => bot.IsHostRole ? LateJoinHost(bot) : LateJoinGuest(bot),
            "rejoin" => bot.IsHostRole ? RejoinHost(bot) : RejoinGuest(bot),
            "requisition" => bot.IsHostRole ? RequisitionHost(bot) : RequisitionGuest(bot),
            "toggles" => bot.IsHostRole ? TogglesHost(bot) : TogglesGuest(bot),
            "espresso" => bot.IsHostRole ? EspressoHost(bot) : EspressoGuest(bot),
            "cups" => bot.IsHostRole ? CupsHost(bot) : CupsGuest(bot),
            "maprejoin" => bot.IsHostRole ? MapRejoinHost(bot) : MapRejoinGuest(bot),
            "emotes" => bot.IsHostRole ? EmotesHost(bot) : EmotesGuest(bot),
            "saveexit" => bot.IsHostRole ? SaveExitHost(bot) : SaveExitGuest(bot),
            "fixes101" => bot.IsHostRole ? Fixes101Host(bot) : Fixes101Guest(bot),
            "mech1705" => bot.IsHostRole ? Mech1705Host(bot) : Mech1705Guest(bot),
            "campaign" => bot.IsHostRole ? CampaignHost(bot) : CampaignGuest(bot),
            "news" => bot.IsHostRole ? NewsHost(bot) : NewsGuest(bot),
            "measurenotes" => bot.IsHostRole ? MeasureNotesHost(bot) : MeasureNotesGuest(bot),
            "language" => bot.IsHostRole ? LanguageHost(bot) : LanguageGuest(bot),
            "progress" => bot.IsHostRole ? ProgressHost(bot) : ProgressGuest(bot),
            "cursor" => CursorScenario(bot),
            "fieldlook" => FieldLook(bot),
            "steamlobby" => SteamLobbyScenario(bot),
            "pickup" => bot.IsHostRole ? PickupHost(bot) : PickupGuest(bot),
            "buttonprobe" => ButtonProbe(bot),
            "rules" => bot.IsHostRole ? RulesHost(bot) : RulesGuest(bot),
            "animprobe" => AnimProbe(bot),
            "compprobe" => CompProbe(bot),
            "magazine" => bot.IsHostRole ? MagazineHost(bot) : MagazineGuest(bot),
            "paint" => bot.IsHostRole ? PaintHost(bot) : PaintGuest(bot),
            "scout" => bot.IsHostRole ? ScoutHost(bot) : ScoutGuest(bot),
            "dice" => bot.IsHostRole ? DiceHost(bot) : DiceGuest(bot),
            "events" => bot.IsHostRole ? EventsHost(bot) : EventsGuest(bot),
            "treeprobe" => TreeProbe(bot),
            "gametab" => GameTab(bot),
            "devtools" => bot.IsHostRole ? DevToolsHost(bot) : DevToolsGuest(bot),
            "sweep" => bot.IsHostRole ? SweepHost(bot) : SweepGuest(bot),
            "lamps" => bot.IsHostRole ? LampsHost(bot) : LampsGuest(bot),
            _ => null,
        };

        /// <summary>One copy, no session: start a mission and let it run (crash bisection, patch smoke test).</summary>
        static IEnumerable<object> SoloMission(Bot bot)
        {
            foreach (var x in StartMission(bot, "Artillery Introduction")) yield return x;
            foreach (var x in bot.Wait(25)) yield return x;
            bot.Check(Sync.MissionFlow.Phase == MissionManager.GamePhase.MissionActive, "still in mission after 25 s");
        }

        static IEnumerable<object> UiShots(Bot bot, string who)
        {
            for (int tab = 0; tab < 3; tab++)
            {
                UI.CoopUI.Show(tab);
                foreach (var x in bot.Wait(0.5)) yield return x;
                foreach (var x in bot.Shot($"{who}-tab{tab}")) yield return x;
            }
            UI.CoopUI.Show(0);
        }

        /// <summary>Two real game copies find each other, agree on the roster and exchange messages.</summary>
        static IEnumerable<object> ConnectHost(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsHost, 30, "hosting")) yield return x;
            foreach (var x in bot.Until(() => Coop.Session.PlayerCount == 2, 90, "guest joined")) yield return x;
            var guest = Coop.Session.GetPlayer(1);
            bot.Check(guest != null && guest.Name == "GuestBot", $"guest name is GuestBot (got '{guest?.Name}')");
            bot.Signal("ping", "42");
            foreach (var x in bot.Until(() => bot.HasSignal("pong"), 20, "pong from guest")) yield return x;
            bot.Check(bot.SignalValue("pong") == "42", "pong echoes ping value");
            foreach (var x in bot.Until(() => guest.RttMs > 0, 10, "rtt measured")) yield return x;
            bot.Note($"rtt {guest.RttMs:F1} ms");
            foreach (var x in UiShots(bot, "host")) yield return x;
            bot.Signal("done");
            foreach (var x in bot.Wait(1)) yield return x;
        }

        static IEnumerable<object> ConnectGuest(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsGuest, 90, "connected to host")) yield return x;
            foreach (var x in bot.Until(() => Coop.Session.PlayerCount == 2 && Coop.Session.GetPlayer(0)?.Name == "HostBot", 20, "roster shows HostBot")) yield return x;
            bot.Check(Coop.Session.LocalSlot == 1, $"guest got slot 1 (got {Coop.Session.LocalSlot})");
            foreach (var x in bot.Until(() => bot.HasSignal("ping"), 20, "ping from host")) yield return x;
            bot.Signal("pong", bot.SignalValue("ping"));
            foreach (var x in UiShots(bot, "guest")) yield return x;
            foreach (var x in bot.Until(() => bot.HasSignal("done"), 30, "host done")) yield return x;
        }
    }
}
