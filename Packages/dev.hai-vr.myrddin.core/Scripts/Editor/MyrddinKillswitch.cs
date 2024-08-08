using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Hai.Myrddin.Core.Runtime;
using HarmonyLib;
using JetBrains.Annotations;
using UdonSharp;
using UdonSharp.Compiler;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using UnityEngine.XR.Management;
using VRC.Udon;

namespace Hai.Myrddin.Core.Editor
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
        public static bool ClientSimVR
        {
            get => PlayerPrefs.GetInt($"Hai.Myrddin.{nameof(ClientSimVR)}") > 0;
            set => PlayerPrefs.SetInt($"Hai.Myrddin.{nameof(ClientSimVR)}", value ? 1 : 0);
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
            Debug.Log("(MyrddinKillswitch) Executing static initializer.");
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
            // PreventUdonSharpFromPostProcessingScene();
            // PreventUdonSharpCompilerFromDetectingPlayMode();
            PreventCustomUdonSharpDrawerFromRegistering();
            PreventUdonSharpFromCopyingUdonToProxy();
            // FIXME: When building a world, this needs to be restored prior to the build.
            PreventVRChatEnvConfigFromSettingOculusLoaderAsTheXRPluginProvider();
            
            RedirectUiEventsToUdonBehaviour();
            HijackInputGetAxis();
            RegisterGetAxis();
            
            EditorApplication.playModeStateChanged -= DisableUdonManager;
            EditorApplication.playModeStateChanged += DisableUdonManager;
            
            Debug.Log("(MyrddinKillswitch) Killswitch is ON.");
        }

        private static void RegisterGetAxis()
        {
            RegisterInputGetAxisFunction("Oculus_CrossPlatform_PrimaryIndexTrigger", _ => MyrddinInput.LeftTrigger);
            RegisterInputGetAxisFunction("Oculus_CrossPlatform_SecondaryIndexTrigger", _ => MyrddinInput.RightTrigger);
            RegisterInputGetAxisFunction("Oculus_CrossPlatform_PrimaryHandTrigger", _ => MyrddinInput.LeftGrip);
            RegisterInputGetAxisFunction("Oculus_CrossPlatform_SecondaryHandTrigger", _ => MyrddinInput.RightGrip);
            RegisterInputGetAxisFunction("Oculus_CrossPlatform_PrimaryThumbstickHorizontal", _ => MyrddinInput.LeftAxisX);
            RegisterInputGetAxisFunction("Oculus_CrossPlatform_PrimaryThumbstickVertical", _ => MyrddinInput.LeftAxisY);
            RegisterInputGetAxisFunction("Oculus_CrossPlatform_SecondaryThumbstickHorizontal", _ => MyrddinInput.RightAxisX);
            RegisterInputGetAxisFunction("Oculus_CrossPlatform_SecondaryThumbstickVertical", _ => MyrddinInput.RightAxisY);
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

        private static void PreventUdonSharpFromPostProcessingScene()
        {
            var udonSharpToPatch = HackGetTypeByName(EditorManager);
            var theMethodThatIsCalledOnSceneBuild = udonSharpToPatch.GetMethod("OnSceneBuildInternal", BindingFlags.Static | BindingFlags.NonPublic);
            var ourPatch = typeof(MyrddinKillswitch).GetMethod(nameof(PreventExecutionSceneBuild));
            
            DoPatch(theMethodThatIsCalledOnSceneBuild, new HarmonyMethod(ourPatch));
        }

        private static void PreventUdonSharpCompilerFromDetectingPlayMode()
        {
            var udonSharpToPatch = typeof(UdonSharpCompilerV1);
            var theMethodThatIsCalledOnPlayMode = udonSharpToPatch.GetMethod("OnPlayStateChanged", BindingFlags.Static | BindingFlags.NonPublic);
            var ourPatch = typeof(MyrddinKillswitch).GetMethod(nameof(PreventExecutionCompilerPlayMode));
            
            DoPatch(theMethodThatIsCalledOnPlayMode, new HarmonyMethod(ourPatch));
        }

        private static void PreventCustomUdonSharpDrawerFromRegistering()
        {
            var udonSharpToPatch = HackGetTypeByName("UdonSharpEditor.UdonBehaviourDrawerOverride");
            var theMethodThatIsCalledOnPlayMode = udonSharpToPatch.GetMethod("OverrideUdonBehaviourDrawer", BindingFlags.Static | BindingFlags.NonPublic);
            var ourPatch = typeof(MyrddinKillswitch).GetMethod(nameof(PreventCustomUdonSharpDrawer));
            
            DoPatch(theMethodThatIsCalledOnPlayMode, new HarmonyMethod(ourPatch));
        }

        private static void PreventUdonSharpFromCopyingUdonToProxy()
        {
            var udonSharpToPatch = HackGetTypeByName("UdonSharpEditor.UdonSharpEditorUtility");
            Type[] types = new Type[] { typeof(UdonSharpBehaviour), typeof(ProxySerializationPolicy) };
            var theMethodThatIsCalledOnPlayMode = udonSharpToPatch.GetMethod("CopyUdonToProxy", types);
            var ourPatch = typeof(MyrddinKillswitch).GetMethod(nameof(PreventCopyingUdonToProxy));
            
            DoPatch(theMethodThatIsCalledOnPlayMode, new HarmonyMethod(ourPatch));
        }
        
        private static void PreventVRChatEnvConfigFromSettingOculusLoaderAsTheXRPluginProvider()
        {
            // In VRCSDK base EnvConfig.cs, line 1102, this effectively mutes that one specific call to XRPackageMetadataStore.AssignLoader
            var classToPatch = HackGetTypeByName("UnityEditor.XR.Management.Metadata.XRPackageMetadataStore");
            var theMethodThatIsCalledOnPlayMode = classToPatch.GetMethod("AssignLoader");
            var ourPatch = typeof(MyrddinKillswitch).GetMethod(nameof(PreventAssignLoaderFromSettingOculusLoader));
            
            DoPatch(theMethodThatIsCalledOnPlayMode, new HarmonyMethod(ourPatch));
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

        // FIXME: Probably not needed.
        public static bool PreventExecutionSceneBuild(bool isBuildingPlayer)
        {
            Debug.Log("(MyrddinKillswitch) Prevented UdonSharpEditorManager from doing operations on scene build.");
            return false;
        }

        // FIXME: Probably not needed.
        public static bool PreventExecutionCompilerPlayMode(PlayModeStateChange stateChange)
        {
            Debug.Log("(MyrddinKillswitch) Prevented UdonSharpCompilerV1 from doing operations on entering Play mode.");
            return false;
        }

        // FIXME: Probably no longer needed, it was the wrong issue (see PreventCopyingUdonToProxy).
        // Might still cause errors when fields are added or removed if this isn't running.
        public static bool PreventCustomUdonSharpDrawer()
        {
            // FIXME: The comment below is no longer true.
            // This spams errors when in Killswitch mode.
            Debug.Log("(MyrddinKillswitch) Prevented custom UdonSharp drawer from registering.");
            return false;
        }

        public static bool PreventCopyingUdonToProxy(UdonSharpBehaviour proxy, ProxySerializationPolicy serializationPolicy)
        {
            // We need to prevent copying Udon to Proxy, because it copies null values from the runtime UdonBehaviour.
            
            // This is called a lot, so do not log.
            // Debug.Log("(MyrddinKillswitch) Prevented copying Udon to Proxy.");
            
            return false;
        }

        public static bool PreventAssignLoaderFromSettingOculusLoader(XRManagerSettings settings, ref string loaderTypeName, BuildTargetGroup buildTargetGroup)
        {
            // FIXME: When building a world, this needs to be restored prior to the build.
            /*
- Q: "does anyone know why the fuck does the vrchat sdk base package needs to set the oculus loader as the XR provider every time the domain reloads?"
- A: "It kept resetting for people so the SPS-I variants would stop building on android
And on PC it gets enforced to be there so the spatialized gets added too otherwise things spam errors as well
I hate that it’s an asset"
            */
            
            if (loaderTypeName == "Unity.XR.Oculus.OculusLoader")
            {
                // In VRCSDK base EnvConfig.cs, line 1102, this effectively mutes calls similar to this to XRPackageMetadataStore.AssignLoader
                
                var trace = new System.Diagnostics.StackTrace();
                var stackFrames = trace.GetFrames();
                if (stackFrames != null)
                {
                    foreach (var frame in stackFrames)
                    {
                        var declaringType = frame.GetMethod().DeclaringType;
                        if (declaringType != null && declaringType.FullName == "VRC.Editor.EnvConfig")
                        {
                            // FIXME: This is a really messy location to set this
                            if (MyrddinKillswitch.ClientSimVR)
                            {
                                // FIXME: Is this the right location to set this?
                                if (XRGeneralSettings.Instance)
                                {
                                    XRGeneralSettings.Instance.InitManagerOnStart = ClientSimVR;
                                }
                                
                                Debug.Log("(MyrddinKillswitch) When setting XR Plugin Provider, replaced OculusLoader with OpenXR Loader.");
                                loaderTypeName = "UnityEngine.XR.OpenXR.OpenXRLoader";
                                return true;
                            }
                            
                            Debug.Log("(MyrddinKillswitch) Prevented setting XR Plugin Provider to OculusLoader.");
                            return false;
                        }
                    }
                }
            }
            return true;
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