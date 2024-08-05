using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UdonSharp;
using UnityEditor;
using UnityEngine;
using VRC.Udon;

namespace Hai.Myrddin
{
    [InitializeOnLoad]
    public class MyrddinKillswitch
    {
        private const string HarmonyIdentifier = "dev.hai-vr.myrddin.Harmony";
        private const string EditorManager = "UdonSharpEditor.UdonSharpEditorManager";
        private const string ScriptingDefineForMyrddinActive = "MYRDDIN_ACTIVE";

        private static readonly Harmony Harm;
        private static readonly Dictionary<string, HijackGetAxisFunction> AxisNameToHijackFn = new Dictionary<string, HijackGetAxisFunction>();
        public delegate float HijackGetAxisFunction(string axisName);
        
        private static int _disableUdonManagerAttempt;
        private static FieldInfo _backingUdonBehaviourField;

        static MyrddinKillswitch()
        {
            Harm = new Harmony(HarmonyIdentifier);

            PreventUdonSharpFromMutingNativeBehaviours();
            PreventUdonSharpFromAffectingPlayModeEntry();
            RedirectUiEventsToUdonBehaviour();
            HijackInputGetAxis();
            
            EditorApplication.playModeStateChanged -= DisableUdonManager;
            EditorApplication.playModeStateChanged += DisableUdonManager;
            
            var group = BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget);
            var degenSymbols = PlayerSettings.GetScriptingDefineSymbolsForGroup(group);
            var symbols = new List<string>(degenSymbols.Split(";"));
            if (!symbols.Contains(ScriptingDefineForMyrddinActive))
            {
                symbols.Add(ScriptingDefineForMyrddinActive);
                var newDegenSymbols = string.Join(',', symbols);
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
            
            Harm.Patch(theMethodThatMutesNativeBehaviours, new HarmonyMethod(ourPatch));
        }

        private static void PreventUdonSharpFromAffectingPlayModeEntry()
        {
            var udonSharpToPatch = HackGetTypeByName(EditorManager);
            var theMethodThatAffectsPlayModeEntry = udonSharpToPatch.GetMethod("OnChangePlayMode", BindingFlags.Static | BindingFlags.NonPublic);
            var ourPatch = typeof(MyrddinKillswitch).GetMethod(nameof(PreventExecutionPlayModeEntry));
            
            Harm.Patch(theMethodThatAffectsPlayModeEntry, new HarmonyMethod(ourPatch));
        }

        private static void RedirectUiEventsToUdonBehaviour()
        {
            var udonBehaviourToPatch = typeof(UdonBehaviour);
            var sendCustomEventMethod = udonBehaviourToPatch.GetMethod(nameof(UdonBehaviour.SendCustomEvent));
            
            _backingUdonBehaviourField = typeof(UdonSharpBehaviour).GetField("_udonSharpBackingUdonBehaviour", BindingFlags.Instance | BindingFlags.NonPublic);
            var ourPatch = typeof(MyrddinKillswitch).GetMethod(nameof(RedirectEvent));
            
            Harm.Patch(sendCustomEventMethod, new HarmonyMethod(ourPatch));
        }

        private static void HijackInputGetAxis()
        {
            var inputToPatch = typeof(Input);
            var getAxisMethod = inputToPatch.GetMethod(nameof(Input.GetAxis), BindingFlags.Static | BindingFlags.Public);
            var ourPatch = typeof(MyrddinKillswitch).GetMethod(nameof(HijackGetAxis));
            
            Harm.Patch(getAxisMethod, new HarmonyMethod(ourPatch));
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

        public static bool RedirectEvent(UdonBehaviour __instance, string eventName)
        {
            var sharpies = __instance.transform.GetComponents<UdonSharpBehaviour>();
            foreach (var udonSharpBehaviour in sharpies)
            {
                var corresponding = (UdonBehaviour)_backingUdonBehaviourField.GetValue(udonSharpBehaviour);
                if (corresponding == __instance)
                {
                    Debug.Log($"(MyrddinKillswitch) Redirecting SendCustomEvent({eventName}) to {udonSharpBehaviour.GetType().FullName}.");
                    udonSharpBehaviour.SendCustomEvent(eventName);
                    return false;
                }
            }
            Debug.Log("(MyrddinKillswitch) Failed to redirect SendCustomEvent targeted to UdonBehaviour back to its UdonSharpBehaviour.");
            return true;
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
            
            var plu = UnityEngine.Object.FindObjectOfType<PostLateUpdater>();
            if (plu != null) plu.enabled = false;
        }
    }
}