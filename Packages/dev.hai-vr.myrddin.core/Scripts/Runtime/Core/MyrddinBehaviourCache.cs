using System.Collections.Generic;
using System.Reflection;
using UdonSharp;
using VRC.Udon;

namespace Hai.Myrddin.Core.Runtime
{
    public class MyrddinBehaviourCache
    {
        private static readonly Dictionary<UdonBehaviour, UdonSharpBehaviour> BehaviourCache = new Dictionary<UdonBehaviour, UdonSharpBehaviour>();
        private static readonly FieldInfo BackingUdonBehaviourField;

        static MyrddinBehaviourCache()
        {
            BackingUdonBehaviourField = typeof(UdonSharpBehaviour).GetField("_udonSharpBackingUdonBehaviour", BindingFlags.Instance | BindingFlags.NonPublic);
        }

        public static bool TryGetUdonSharpBehaviour(UdonBehaviour behaviour, out UdonSharpBehaviour found)
        {
            if (BehaviourCache.TryGetValue(behaviour, out var cachedResult))
            {
                if (cachedResult != null) // Can happen when hot reloads are involved
                {
                    found = cachedResult;
                    return true;
                }

                BehaviourCache.Remove(behaviour);
            }
            
            var sharpies = behaviour.transform.GetComponents<UdonSharpBehaviour>();
            foreach (var udonSharpBehaviour in sharpies)
            {
                var corresponding = (UdonBehaviour)BackingUdonBehaviourField.GetValue(udonSharpBehaviour);
                if (corresponding == behaviour)
                {
                    BehaviourCache[behaviour] = udonSharpBehaviour;
                    found = udonSharpBehaviour;
                    return true;
                }
            }

            found = null;
            return false;
        }
    }
}