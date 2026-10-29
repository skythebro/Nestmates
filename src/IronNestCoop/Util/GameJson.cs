using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Newtonsoft.Json;

namespace IronNestCoop.Util
{
    /// <summary>
    /// The game's own Newtonsoft (IL2CPP side) serialises its *SaveData classes for checkpoints, so we
    /// use exactly the same serializer to ship those objects to guests: same fields, same shapes.
    /// </summary>
    public static class GameJson
    {
        public static string Write(Il2CppSystem.Object o) => o == null ? null : JsonConvert.SerializeObject(o);

        public static T Read<T>(string json) where T : Il2CppObjectBase
        {
            if (string.IsNullOrEmpty(json)) return null;
            var o = JsonConvert.DeserializeObject(json, Il2CppType.Of<T>());
            return o?.TryCast<T>();
        }
    }
}
