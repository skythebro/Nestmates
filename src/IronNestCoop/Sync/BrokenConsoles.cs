using System.Collections.Generic;
using IronNestCoop.Core;
using UnityEngine;

namespace IronNestCoop.Sync
{
    /// <summary>
    /// Consoles a mission can break: IronRoad sends "BrakeCalculator" (and other missions "BrakeRequisition"), and a
    /// listener on the console turns on its "Broken state" (smoke, sparks, and a target in front of the controls that
    /// takes the clicks). On the host that stops real presses, but a guest's press is run on the host's button directly
    /// and went straight past it, so guests could still print cards with a broken calculator. The host now refuses a
    /// guest's press or dial turn on a broken console.
    /// </summary>
    public static class BrokenConsoles
    {
        static readonly bool Off = LaunchArgs.Mutant("nobrokengate");   // used by the tests to prove the gate matters

        sealed class Breaker { public Transform Console; public GameObject Broken; public MissionGraphNotificationListener Listener; }

        static List<Breaker> _breakers;
        static int _scene = -1;

        static List<Breaker> Breakers()
        {
            int scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
            if (_breakers != null && scene == _scene && _breakers.TrueForAll(b => b.Listener != null)) return _breakers;
            _scene = scene;
            _breakers = new List<Breaker>();
            foreach (var l in Resources.FindObjectsOfTypeAll<MissionGraphNotificationListener>())
            {
                if (l == null || string.IsNullOrEmpty(l.gameObject.scene.name) || l.OnMessage == null) continue;
                if (l.MessageID == null || !l.MessageID.StartsWith("Brake")) continue;
                for (int i = 0; i < l.OnMessage.GetPersistentEventCount(); i++)
                {
                    var go = l.OnMessage.GetPersistentTarget(i)?.TryCast<GameObject>();
                    if (go == null) continue;
                    // the breaker sits on the console ("Artillery Computer Console/Balistic Calculator Breaker")
                    var console = l.transform.parent != null ? l.transform.parent : l.transform;
                    _breakers.Add(new Breaker { Console = console, Broken = go, Listener = l });
                }
            }
            return _breakers;
        }

        /// <summary>The control belongs to a console the mission has broken.</summary>
        public static bool IsBroken(Component control)
        {
            if (Off || control == null) return false;
            foreach (var b in Breakers())
                if (b.Broken != null && b.Broken.activeInHierarchy && b.Console != null && control.transform.IsChildOf(b.Console)) return true;
            return false;
        }

        /// <summary>Host: the scene notifications that broke consoles so far, for a player joining now.</summary>
        public static IEnumerable<(string path, string message)> BrokenNow()
        {
            foreach (var b in Breakers())
                if (b.Broken != null && b.Broken.activeInHierarchy && b.Listener != null)
                    yield return (Util.ScenePath.Of(b.Listener.transform), b.Listener.MessageID);
        }
    }
}
