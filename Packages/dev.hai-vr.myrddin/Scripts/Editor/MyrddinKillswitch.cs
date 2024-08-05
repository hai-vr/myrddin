using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UdonSharp;
using UnityEditor;
using UnityEngine;
using VRC.Udon;

[InitializeOnLoad]
public class MyrddinKillswitch
{
    private static readonly Harmony Harm;
    private const string HarmonyIdentifier = "dev.hai-vr.myrddin.Harmony";
    
    private static int _attempt;
    private static FieldInfo _backingField;

    static MyrddinKillswitch()
    {
        Harm = new Harmony(HarmonyIdentifier);

        PreventUdonSharpFromMutingNativeBehaviours();
        RedirectUiEventsToUdonBehaviour();
        
        EditorApplication.playModeStateChanged -= DisableUdonManager;
        EditorApplication.playModeStateChanged += DisableUdonManager;
    }

    private static void PreventUdonSharpFromMutingNativeBehaviours()
    {
        var udonSharp = HackGetTypeByName("UdonSharpEditor.UdonSharpEditorManager");
        var theMethodThatMutesNativeBehaviours = udonSharp.GetMethod("RunPostAssemblyBuildRefresh", BindingFlags.Static | BindingFlags.NonPublic);
        var ourPatch = typeof(MyrddinKillswitch).GetMethod(nameof(PreventExecution));
        
        Harm.Patch(theMethodThatMutesNativeBehaviours, new HarmonyMethod(ourPatch));
    }

    private static void RedirectUiEventsToUdonBehaviour()
    {
        var udonSharp = typeof(UdonBehaviour);
        var sendCustomEventMethod = udonSharp.GetMethod("SendCustomEvent");
        
        _backingField = typeof(UdonSharpBehaviour).GetField("_udonSharpBackingUdonBehaviour", BindingFlags.Instance | BindingFlags.NonPublic);
        var ourPatch = typeof(MyrddinKillswitch).GetMethod(nameof(RedirectEvent));
        
        Harm.Patch(sendCustomEventMethod, new HarmonyMethod(ourPatch));
    }

    public static bool PreventExecution()
    {
        Debug.Log("(MyrddinKillswitch) Prevented UdonSharpEditorManager from muting behaviours.");
        return false;
    }

    public static bool RedirectEvent(UdonBehaviour __instance, string eventName)
    {
        var sharpies = __instance.transform.GetComponents<UdonSharpBehaviour>();
        foreach (var udonSharpBehaviour in sharpies)
        {
            var corresponding = (UdonBehaviour)_backingField.GetValue(udonSharpBehaviour);
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
            if (_attempt < 100) // Completely arbitrary number. It's not supposed to fail at all.
            {
                Debug.Log("(MyrddinKillswitch) Attempt to disable UdonManager failed. Will try again next frame.");
                EditorApplication.update += TryDisableUdonManager;
                _attempt++;
            }
            else
            {
                Debug.Log($"(MyrddinKillswitch) Failed to disable UdonManager after {_attempt} attemps, giving up.");
            }
            return;
        }

        manager.enabled = false;
        Debug.Log("(MyrddinKillswitch) UdonManager has been disabled.");
        
        var plu = UnityEngine.Object.FindObjectOfType<PostLateUpdater>();
        if (plu != null) plu.enabled = false;
    }
}
