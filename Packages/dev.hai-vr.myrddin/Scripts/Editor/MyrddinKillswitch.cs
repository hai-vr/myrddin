using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using JetBrains.Annotations;
using UdonSharp;
using UnityEditor;
using UnityEngine;
using VRC.Udon;

namespace Hai.Myrddin
{
    [InitializeOnLoad]
    public class MyrddinKillswitch
    {
        public static bool UseKillswitch
        {
            get => PlayerPrefs.GetInt($"Hai.Myrddin.{nameof(UseKillswitch)}") > 0;
            set
            {
                PlayerPrefs.SetInt($"Hai.Myrddin.{nameof(UseKillswitch)}", value ? 1 : 0);
                EnsureScriptingDefineIsSetTo(value);
                
                // Unfortunately, this can't be changed live, because U# registers the Harmony patches after post-assembly refresh.
                // Maybe there's another way.
                // UpdateKillswitch();
            }
        }

        private const string HarmonyIdentifier = "dev.hai-vr.myrddin.Harmony";
        private const string EditorManager = "UdonSharpEditor.UdonSharpEditorManager";
        private const string ScriptingDefineForMyrddinActive = "MYRDDIN_ACTIVE";
        private const string ReflectionPrefix = "__";

        private static readonly Harmony Harm;
        private static readonly Dictionary<string, HijackGetAxisFunction> AxisNameToHijackFn = new Dictionary<string, HijackGetAxisFunction>();
        private static readonly Dictionary<UdonBehaviour, UdonSharpBehaviour> BehaviourCache = new Dictionary<UdonBehaviour, UdonSharpBehaviour>();
        private static readonly List<MemorizedPatch> RememberPatches = new List<MemorizedPatch>();
        public delegate float HijackGetAxisFunction(string axisName);
        
        private static int _disableUdonManagerAttempt;
        private static FieldInfo _backingUdonBehaviourField;
        private static bool _prevKillswitch;

        static MyrddinKillswitch()
        {
            Harm = new Harmony(HarmonyIdentifier);
            
            EnsureScriptingDefineIsSetTo(UseKillswitch);

            UpdateKillswitch();
        }

        private static void UpdateKillswitch()
        {
            var currentKillswitch = UseKillswitch;
            if (currentKillswitch == _prevKillswitch) return;
            _prevKillswitch = currentKillswitch;
            
            if (currentKillswitch)
            {
                EnableHooks();
            }
            else
            {
                DisableHooks();
            }
        }

        private static void EnableHooks()
        {
            PreventUdonSharpFromMutingNativeBehaviours();
            PreventUdonSharpFromAffectingPlayModeEntry();
            RedirectUiEventsToUdonBehaviour();
            HijackInputGetAxis();
            
            EditorApplication.playModeStateChanged -= DisableUdonManager;
            EditorApplication.playModeStateChanged += DisableUdonManager;
            
            Debug.Log("(MyrddinKillswitch) Killswitch is ON.");
        }

        private static void DisableHooks()
        {
            foreach (var memorizedPatch in RememberPatches)
            {
                Harm.Unpatch(memorizedPatch.Source, memorizedPatch.Destination.method);
            }
            RememberPatches.Clear();
            
            EditorApplication.playModeStateChanged -= DisableUdonManager;
            
            Debug.Log("(MyrddinKillswitch) Killswitch is OFF.");
        }

#if MYRDDIN_HOT_RELOAD_EXISTS
        [SingularityGroup.HotReload.InvokeOnHotReload]
        [UsedImplicitly]
        public static void HandleMethodPatches()
        {
            return;
            
            Debug.Log("(MyrddinKillswitch) Hot reload detected, re-applying patches...");
            if (UseKillswitch)
            {
                DisableHooks();
                EnableHooks();
            }
        }
#endif

        private static void EnsureScriptingDefineIsSetTo(bool isActive)
        {
            var group = BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget);
            var degenSymbols = PlayerSettings.GetScriptingDefineSymbolsForGroup(group);
            var symbols = new List<string>(degenSymbols.Split(";"));
            if (isActive)
            {
                if (!symbols.Contains(ScriptingDefineForMyrddinActive))
                {
                    symbols.Add(ScriptingDefineForMyrddinActive);
                    var newDegenSymbols = string.Join(';', symbols);
                    PlayerSettings.SetScriptingDefineSymbolsForGroup(group, newDegenSymbols);
                }
            }
            else
            {
                while (symbols.Contains(ScriptingDefineForMyrddinActive))
                {
                    symbols.Remove(ScriptingDefineForMyrddinActive);
                }
                var newDegenSymbols = string.Join(';', symbols);
                PlayerSettings.SetScriptingDefineSymbolsForGroup(group, newDegenSymbols);
            }
        }

        public static void RegisterInputGetAxisFunction(string axisName, HijackGetAxisFunction hijackFn)
        {
            AxisNameToHijackFn[axisName] = hijackFn;
        }

        private static void PreventUdonSharpFromMutingNativeBehaviours()
        {
            var udonSharpToPatch = HackGetTypeByName(EditorManager);
            var theMethodThatMutesNativeBehaviours = udonSharpToPatch.GetMethod("RunPostAssemblyBuildRefresh", BindingFlags.Static | BindingFlags.NonPublic);
            var ourPatch = typeof(MyrddinKillswitch).GetMethod(nameof(PreventExecutionMuteBehaviours));
            
            DoPatch(theMethodThatMutesNativeBehaviours, new HarmonyMethod(ourPatch));
        }

        private static void PreventUdonSharpFromAffectingPlayModeEntry()
        {
            var udonSharpToPatch = HackGetTypeByName(EditorManager);
            var theMethodThatAffectsPlayModeEntry = udonSharpToPatch.GetMethod("OnChangePlayMode", BindingFlags.Static | BindingFlags.NonPublic);
            var ourPatch = typeof(MyrddinKillswitch).GetMethod(nameof(PreventExecutionPlayModeEntry));
            
            DoPatch(theMethodThatAffectsPlayModeEntry, new HarmonyMethod(ourPatch));
        }

        private static void RedirectUiEventsToUdonBehaviour()
        {
            _backingUdonBehaviourField = typeof(UdonSharpBehaviour).GetField("_udonSharpBackingUdonBehaviour", BindingFlags.Instance | BindingFlags.NonPublic);

            var methodsToPatch = typeof(MyrddinKillswitch).GetMethods()
                .Where(info => info.Name.StartsWith(ReflectionPrefix))
                .Select(info => info.Name.Substring(ReflectionPrefix.Length))
                .ToArray();
            
            PatchThese(methodsToPatch);
        }

        // ReSharper disable InconsistentNaming
        // All methods here starting with __ (which is ReflectionPrefix) will be found through reflection and wired to the corresponding UdonBehaviour method. 
        [UsedImplicitly] public static bool __SendCustomEvent(UdonBehaviour __instance, string eventName) => Execute(__instance, behaviour => behaviour.SendCustomEvent(eventName));
        [UsedImplicitly] public static bool __Interact(UdonBehaviour __instance) => Execute(__instance, behaviour => behaviour.Interact());
        [UsedImplicitly] public static bool __OnPickup(UdonBehaviour __instance) => Execute(__instance, behaviour => behaviour.OnPickup());
        [UsedImplicitly] public static bool __OnDrop(UdonBehaviour __instance) => Execute(__instance, behaviour => behaviour.OnDrop());
        [UsedImplicitly] public static bool __OnPickupUseDown(UdonBehaviour __instance) => Execute(__instance, behaviour => behaviour.OnPickupUseDown());
        [UsedImplicitly] public static bool __OnPickupUseUp(UdonBehaviour __instance) => Execute(__instance, behaviour => behaviour.OnPickupUseUp());
        // ReSharper restore InconsistentNaming

        private static void PatchThese(string[] thingsToPatch)
        {
            var toPatch = typeof(UdonBehaviour);
            var ourType = typeof(MyrddinKillswitch);
            foreach (var from in thingsToPatch)
            {
                DoPatch(toPatch.GetMethod(from), new HarmonyMethod(ourType.GetMethod($"{ReflectionPrefix}{from}")));
            }
        }

        private static void HijackInputGetAxis()
        {
            var inputToPatch = typeof(Input);
            var getAxisMethod = inputToPatch.GetMethod(nameof(Input.GetAxis), BindingFlags.Static | BindingFlags.Public);
            var ourPatch = typeof(MyrddinKillswitch).GetMethod(nameof(HijackGetAxis));
            
            DoPatch(getAxisMethod, new HarmonyMethod(ourPatch));
        }

        private static void DoPatch(MethodInfo source, HarmonyMethod destination)
        {
            Harm.Patch(source, destination);
            
            RememberPatches.Add(new MemorizedPatch
            {
                Source = source,
                Destination = destination
            });
        }

        public static bool PreventExecutionMuteBehaviours()
        {
            Debug.Log("(MyrddinKillswitch) Prevented UdonSharpEditorManager from muting behaviours.");
            return false;
        }

        public static bool PreventExecutionPlayModeEntry()
        {
            Debug.Log("(MyrddinKillswitch) Prevented UdonSharpEditorManager from affecting play mode entry.");
            return false;
        }

        public static bool Execute(UdonBehaviour __instance, Action<UdonSharpBehaviour> doFn)
        {
            if (TryGetUdonSharpBehaviour(__instance, out var udonSharpBehaviour))
            {
                doFn.Invoke(udonSharpBehaviour);
                return false; // Reminder: This is a Harmony patching method. false prevents the original UdonBehaviour from executing
            }
            
            return true;
        }

        private static bool TryGetUdonSharpBehaviour(UdonBehaviour behaviour, out UdonSharpBehaviour found)
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
                var corresponding = (UdonBehaviour)_backingUdonBehaviourField.GetValue(udonSharpBehaviour);
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

        // ReSharper disable once InconsistentNaming
        public static bool HijackGetAxis(string axisName, ref float __result)
        {
            if (AxisNameToHijackFn.TryGetValue(axisName, out var getAxisFn))
            {
                __result = getAxisFn.Invoke(axisName);
                return false;
            }
            
            return true;
        }

        private static Type HackGetTypeByName(string typeName)
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(assembly => assembly.GetTypes())
                .FirstOrDefault(t => t.FullName == typeName);
        }

        private static void DisableUdonManager(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.EnteredPlayMode)
            {
                TryDisableUdonManager();
            }
        }

        private static void TryDisableUdonManager()
        {
            if (!EditorApplication.isPlaying) return;
            
            EditorApplication.update -= TryDisableUdonManager;

            var manager = UnityEngine.Object.FindObjectOfType<UdonManager>();
            if (manager == null)
            {
                if (_disableUdonManagerAttempt < 100) // Completely arbitrary number. It's not supposed to fail at all.
                {
                    Debug.Log("(MyrddinKillswitch) Attempt to disable UdonManager failed. Will try again next frame.");
                    EditorApplication.update += TryDisableUdonManager;
                    _disableUdonManagerAttempt++;
                }
                else
                {
                    Debug.Log($"(MyrddinKillswitch) Failed to disable UdonManager after {_disableUdonManagerAttempt} attemps, giving up.");
                }
                return;
            }

            manager.enabled = false;
            Debug.Log("(MyrddinKillswitch) UdonManager has been disabled.");
            
            // FIXME: We need to run PostLateUpdate on UdonSharpBehaviour
            var plu = UnityEngine.Object.FindObjectOfType<PostLateUpdater>();
            if (plu != null) plu.enabled = false;
        }
        
        private struct MemorizedPatch
        {
            internal MethodInfo Source;
            internal HarmonyMethod Destination;
        }
    }
}