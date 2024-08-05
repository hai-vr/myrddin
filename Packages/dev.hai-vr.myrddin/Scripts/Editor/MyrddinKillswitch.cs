using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEditor;
using UnityEngine;
using VRC.Udon;

[InitializeOnLoad]
public class MyrddinKillswitch
{
    private static readonly Harmony Harm;
    private const string HarmonyIdentifier = "dev.hai-vr.myrddin.Harmony";
    
    private static int _attempt;

    static MyrddinKillswitch()
    {
        Harm = new Harmony(HarmonyIdentifier);

        PreventUdonSharpFromMutingNativeBehaviours();
        
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

    public static bool PreventExecution()
    {
        Debug.Log("(MyrddinKillswitch) Prevented UdonSharpEditorManager from muting behaviours.");
        return false;
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
