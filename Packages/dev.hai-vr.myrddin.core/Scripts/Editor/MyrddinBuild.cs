using System.Reflection;
using UnityEditor;
using UnityEngine;
using VRC.SDKBase.Editor.BuildPipeline;

namespace Hai.Myrddin.Core.Editor
{
    public class MyrddinBuild : IVRCSDKBuildRequestedCallback
    {
        private const string MsgNotAllowedKillswitchIsOn = "You are not allowed to build while the Myrddin Killswitch is ON.";
        public int callbackOrder => 90;
        
        public bool OnBuildRequested(VRCSDKRequestedBuildType requestedBuildType)
        {
            if (requestedBuildType == VRCSDKRequestedBuildType.Avatar) return true;

            if (MyrddinKillswitch.UseKillswitch)
            {
                Debug.LogError(MsgNotAllowedKillswitchIsOn);
                typeof(SceneView).GetMethod("ShowNotification", BindingFlags.NonPublic | BindingFlags.Static)
                    .Invoke(null, new object[] { MsgNotAllowedKillswitchIsOn });
                return false;
            }
            
            // TODO: Make sure the OpenVR Loader is set in the project.
            // It might be too late to set it in this callback though.
            return true;
        }
    }
}