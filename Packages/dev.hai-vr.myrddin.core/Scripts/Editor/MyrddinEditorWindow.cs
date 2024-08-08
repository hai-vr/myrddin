using System;
using System.Collections.Generic;
using System.Linq;
using UdonSharp;
using UnityEditor;
using UnityEngine;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using VRC.Udon;

namespace Hai.Myrddin.Core.Editor
{
    public class MyrddinEditorWindow : EditorWindow
    {
        private static Dictionary<AbstractUdonProgramSource, List<UdonBehaviour>> _notSharpies;
        
        static MyrddinEditorWindow()
        {
            EditorApplication.playModeStateChanged -= PlayModeStateChanged;
            EditorApplication.playModeStateChanged += PlayModeStateChanged;
        }

        private static void PlayModeStateChanged(PlayModeStateChange obj)
        {
            if (obj == PlayModeStateChange.EnteredPlayMode)
            {
                _notSharpies = FindObjectsByType<UdonBehaviour>(FindObjectsSortMode.None)
                    .Where(behaviour => behaviour.programSource != null)
                    .Where(behaviour => !(behaviour.programSource is UdonSharpProgramAsset))
                    .GroupBy(notSharp => notSharp.programSource)
                    .ToDictionary(grouping => grouping.Key, grouping => new List<UdonBehaviour>(grouping));
                
                // Causes the Myrddin window to be repainted
                UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
            }
        }

        private Vector2 _scrollPos;

        private void OnGUI()
        {
            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos, GUILayout.Height(position.height - EditorGUIUtility.singleLineHeight));

            var isEnabled = MyrddinKillswitch.UseKillswitch;
            if (ColoredBackground(isEnabled, Color.red, () => GUILayout.Button(isEnabled ? "Killswitch is ON" : "Killswitch is OFF")))
            {
                MyrddinKillswitch.UseKillswitch = !isEnabled;
            }

            var currentClientSimVR = MyrddinKillswitch.ClientSimVR;
            var newClientSimVR = EditorGUILayout.Toggle("ClientSim VR", MyrddinKillswitch.ClientSimVR);
            if (currentClientSimVR != newClientSimVR)
            {
                MyrddinKillswitch.ClientSimVR = newClientSimVR;
                XRGeneralSettings.Instance.InitManagerOnStart = newClientSimVR;

                if (newClientSimVR)
                {
                    // TODO: Detect if OpenXR loader is installed, and ask to install it when it is not.
                    var loaders = new List<XRLoader> { CreateInstance<OpenXRLoader>() };
                    XRGeneralSettings.Instance.Manager.TrySetLoaders(loaders);
                }
            }

            if (isEnabled)
            {
                EditorGUILayout.HelpBox(@"Killswitch is ON. Udon is suspended.
- UdonSharpBehaviours execute as C# MonoBehaviours.
- UdonManager is disabled.
- Udon Graphs and CyanTrigger are not operational.
- Use `#if !COMPILER_UDONSHARP` to execute non-Udon code.", MessageType.Warning);
                
                if (Application.isPlaying && _notSharpies != null)
                {
                    foreach (var programToObjects in _notSharpies)
                    {
                        var paths = string.Join('\n', programToObjects.Value
                            .Select(obj => AnimationUtility.CalculateTransformPath(obj.transform, null)));

                        EditorGUILayout.HelpBox($"The program {programToObjects.Key.name} is not UdonSharp, so it will not run:\n{paths}", MessageType.Warning);
                    }
                }
            }
            
            EditorGUILayout.EndScrollView();
        }

        private static T ColoredBackground<T>(bool isActive, Color bgColor, Func<T> inside)
        {
            var col = GUI.color;
            try
            {
                if (isActive) GUI.color = bgColor;
                return inside();
            }
            finally
            {
                GUI.color = col;
            }
        }
        
        [MenuItem("Tools/Myrddin/Settings")]
        public static void Settings()
        {
            Obtain().Show();
        }

        private static MyrddinEditorWindow Obtain()
        {
            var editor = GetWindow<MyrddinEditorWindow>(false, null, false);
            editor.titleContent = new GUIContent("Myrddin");
            return editor;
        }
    }
}