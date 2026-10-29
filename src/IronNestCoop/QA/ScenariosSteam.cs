using System.Collections.Generic;
using IronNestCoop.Core;
using IronNestCoop.Steam;

namespace IronNestCoop.QA
{
    /// <summary>
    /// One copy, real Steam (a real account): host an invite-only lobby and get its id back through the
    /// game's Steam dispatcher, then leave. Run with `coopqa.py solo steamlobby`. Nobody can see or join it.
    /// </summary>
    public static partial class Scenarios
    {
        static IEnumerable<object> SteamLobbyScenario(Bot bot)
        {
            foreach (var x in bot.Until(() => SteamApi.Available, 60, "Steam ready")) yield return x;
            Coop.HostSteam(SteamLobby.Visibility.InviteOnly);
            foreach (var x in bot.Until(() => Coop.InSession || Coop.LastError != null, 30, "lobby created or failed")) yield return x;
            bot.Check(Coop.InSession && Coop.Lobby != null && Coop.Lobby.Id != 0, $"Steam lobby created (id {Coop.Lobby?.Id}, error '{Coop.LastError}')");
            bot.Check(Coop.Lobby == null || Coop.Lobby.Owner == SteamApi.LocalId, "we own the lobby");
            // The join path's call result (LobbyEnter_t): re-enter our own lobby with a second handle.
            if (Coop.Lobby != null && Coop.Lobby.Id != 0)
            {
                var probe = new SteamLobby();
                bool? entered = null;
                probe.Join(Coop.Lobby.Id, ok => entered = ok);
                foreach (var x in bot.Until(() => { probe.Tick(); return entered != null; }, 25, "join call answered", soft: true)) yield return x;
                bot.Check(entered != null, $"join call result delivered (entered {entered}, error '{probe.Error}')");
                bot.Note($"join probe: entered {entered}, id {probe.Id}, error '{probe.Error}'");
            }
            foreach (var x in bot.Wait(2)) yield return x;
            Coop.Leave();
            foreach (var x in bot.Wait(2)) yield return x;
            bot.Check(Coop.Lobby == null || Coop.Lobby.Id == 0, "left the lobby");
        }
    }
}
