using IronNestCoop.Core;
using UnityEngine;

namespace IronNestCoop.UI
{
    /// <summary>Small crew list in the corner while playing (window closed).</summary>
    public static class PlayerHud
    {
        /// <summary>Returns where the next line may go (under the list).</summary>
        public static float Draw()
        {
            float y = 28;
            if (!Settings.ShowHud || !Coop.InSession) return y;
            foreach (var p in Coop.Session.Players)
            {
                var info = PlayerInfo.Decode(p.IsLocal ? PlayerInfo.Encode() : p.Info);
                Theme.Dot(new Rect(12, y, 10, 18), Theme.PlayerColor(info.Color), 8);
                string ping = p.IsLocal ? "" : $"  {p.RttMs:F0} ms";
                GUI.Label(new Rect(26, y, 300, 18), p.Name + (p.Slot == 0 ? " (host)" : "") + ping, Theme.Small);
                y += 18;
            }
            return y;
        }
    }
}
