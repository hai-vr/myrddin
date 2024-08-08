using System;
using System.Collections.Generic;
using UdonSharp;
using UnityEngine;

namespace Hai.Myrddin.Core.Runtime
{
    [DefaultExecutionOrder(31000)] // Same as Udon's PostLateUpdater
    public class MyrddinPostLateUpdateExecutor : MonoBehaviour
    {
        private static readonly HashSet<UdonSharpBehaviour> PostLateUpdateCapable = new HashSet<UdonSharpBehaviour>();
        private static readonly Dictionary<Type, bool> _dictionary = new Dictionary<Type, bool>();

        public static void OnNewUdonSharpBehaviourIntroduced(UdonSharpBehaviour sharp)
        {
            if (IsPostLateUpdateOverridden(sharp))
            {
                PostLateUpdateCapable.Add(sharp);
            }
        }

        private static bool IsPostLateUpdateOverridden(UdonSharpBehaviour sharp)
        {
            var runtimeType = sharp.GetType();

            if (_dictionary.TryGetValue(runtimeType, out var isOverridden)) return isOverridden;

            var result = InternalIsPostLateUpdateOverridden(runtimeType);
            _dictionary[runtimeType] = result;
            
            return result;
        }

        private static bool InternalIsPostLateUpdateOverridden(Type runtimeType)
        {
            var postLateUpdateMethod = runtimeType.GetMethod("PostLateUpdate");
            if (postLateUpdateMethod == null) return false;
            
            return postLateUpdateMethod.DeclaringType != typeof(UdonSharpBehaviour);
        }

        private void LateUpdate()
        {
            var toRemove = new List<UdonSharpBehaviour>();
            foreach (var sharp in PostLateUpdateCapable)
            {
                if (sharp == null) // This is an object lifetime check, i.e. the behaviour is deleted.
                {
                    toRemove.Add(sharp);
                }
                else
                {
                    // TODO: Could we make sure that PostLateUpdateCapable list only contains active objects,
                    // by also hooking into OnDisable in the reflective UdonBehaviour hook? 
                    if (sharp.isActiveAndEnabled)
                    {
                        sharp.PostLateUpdate();
                    }
                }
            }
            
            foreach (var deleted in toRemove)
            {
                PostLateUpdateCapable.Remove(deleted);
            }
        }
    }
}