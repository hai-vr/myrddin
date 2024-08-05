using System;
using UnityEditor;
using UnityEngine;

namespace Hai.Myrddin
{
    public class MyrddinEditorWindow : EditorWindow
    {
        
        private Vector2 _scrollPos;

        private void OnGUI()
        {
            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos, GUILayout.Height(position.height - EditorGUIUtility.singleLineHeight));

            var isEnabled = MyrddinKillswitch.UseKillswitch;
            if (ColoredBackground(isEnabled, Color.red, () => GUILayout.Button(isEnabled ? "Killswitch is ON" : "Killswitch is OFF")))
            {
                MyrddinKillswitch.UseKillswitch = !isEnabled;
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
            editor.titleContent = new GUIContent("Myrddin Killswitch");
            return editor;
        }
    }
}