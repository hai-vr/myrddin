using System;
using System.Collections;
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
using VRC.SDK3.ClientSim;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common.Enums;
using VRC.Udon.Common.Interfaces;

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
            // This key used as a magic constant in MyrddinClientSimPatcher
            get => PlayerPrefs.GetInt("Hai.Myrddin.ClientSimVR") > 0;
            set => PlayerPrefs.SetInt("Hai.Myrddin.ClientSimVR", value ? 1 : 0);
        }

        private const string HarmonyIdentifier = "dev.hai-vr.myrddin.core.Harmony";
        private const string EditorManager = "UdonSharpEditor.UdonSharpEditorManager";
        private const string ScriptingDefineForMyrddinActive = "MYRDDIN_ACTIVE";
        private const string ReflectionPrefix = "__";
        private const string ImplementPrefix = "SharpFix__";

        private static readonly Harmony Harm;
        private static readonly Dictionary<string, HijackGetAxisFunction> AxisNameToHijackFn = new Dictionary<string, HijackGetAxisFunction>();
        private static readonly List<MemorizedPatch> RememberPatches = new List<MemorizedPatch>();
        public delegate float HijackGetAxisFunction(string axisName);
        
        private static int _disableUdonManagerAttempt;
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
            // PreventUdonSharpFromPostProcessingScene();
            // PreventUdonSharpCompilerFromDetectingPlayMode();
            PreventCustomUdonSharpDrawerFromRegistering();
            PreventUdonSharpFromCopyingUdonToProxy();
            // FIXME: When building a world, this needs to be restored prior to the build.
            PreventVRChatEnvConfigFromSettingOculusLoaderAsTheXRPluginProvider();
            
            RedirectUiEventsToUdonBehaviour();
            ImplementDelaysInUdonSharpBehaviour();
            // EditorApplication.playModeStateChanged -= InjectOrRemovePostLateUpdatePatch;
            // EditorApplication.playModeStateChanged += InjectOrRemovePostLateUpdatePatch;
            HijackInputGetAxis();
            RegisterGetAxis();
            
            EditorApplication.playModeStateChanged -= DisableUdonManager;
            EditorApplication.playModeStateChanged += DisableUdonManager;
            
            Debug.Log("(MyrddinKillswitch) Killswitch is ON.");
        }

        private static void InjectOrRemovePostLateUpdatePatch(PlayModeStateChange obj)
        {
            if (obj == PlayModeStateChange.ExitingEditMode)
            {
                DetectNewUdonSharpBehavioursThatNeedPostLateUpdate__ThroughConstructor();
            }
            else if (obj == PlayModeStateChange.ExitingPlayMode)
            {
                RemovePostLateUpdatePatch();
            }
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
            // During normal operation, UdonSharp will mute all UdonSharpBehaviour events, so that those of the UdonBehaviour will execute instead.
            // We want to prevent this from happening, so that the native code will execute instead.
            
            var udonSharpToPatch = HackGetTypeByName(EditorManager);
            var theMethodThatMutesNativeBehaviours = udonSharpToPatch.GetMethod("RunPostAssemblyBuildRefresh", BindingFlags.Static | BindingFlags.NonPublic);
            var ourPatch = typeof(MyrddinKillswitch).GetMethod(nameof(PatchExecutionMuteBehaviours));
            
            DoPatch(theMethodThatMutesNativeBehaviours, new HarmonyMethod(ourPatch));
        }

        private static void PreventUdonSharpFromAffectingPlayModeEntry()
        {
            // During normal operation, UdonSharp will prevent entry to Play mode if there are errors in UdonSharp files.
            // We want to allow errors to exist in UdonSharp files, as we want the native implementation to run instead.
            //
            // (If there are errors as seen by the UdonSharp, the creator will have to fix them during normal operation)
            // (The creator can also use #if !COMPILER_UDONSHARP to execute native-specific code)
            
            var udonSharpToPatch = HackGetTypeByName(EditorManager);
            var theMethodThatAffectsPlayModeEntry = udonSharpToPatch.GetMethod("OnChangePlayMode", BindingFlags.Static | BindingFlags.NonPublic);
            var ourPatch = typeof(MyrddinKillswitch).GetMethod(nameof(PatchExecutionPlayModeEntry));
            
            DoPatch(theMethodThatAffectsPlayModeEntry, new HarmonyMethod(ourPatch));
        }

        private static void PreventUdonSharpFromPostProcessingScene()
        {
            var udonSharpToPatch = HackGetTypeByName(EditorManager);
            var theMethodThatIsCalledOnSceneBuild = udonSharpToPatch.GetMethod("OnSceneBuildInternal", BindingFlags.Static | BindingFlags.NonPublic);
            var ourPatch = typeof(MyrddinKillswitch).GetMethod(nameof(PatchExecutionSceneBuild));
            
            DoPatch(theMethodThatIsCalledOnSceneBuild, new HarmonyMethod(ourPatch));
        }

        private static void PreventUdonSharpCompilerFromDetectingPlayMode()
        {
            var udonSharpToPatch = typeof(UdonSharpCompilerV1);
            var theMethodThatIsCalledOnPlayMode = udonSharpToPatch.GetMethod("OnPlayStateChanged", BindingFlags.Static | BindingFlags.NonPublic);
            var ourPatch = typeof(MyrddinKillswitch).GetMethod(nameof(PatchExecutionCompilerPlayMode));
            
            DoPatch(theMethodThatIsCalledOnPlayMode, new HarmonyMethod(ourPatch));
        }

        private static void PreventCustomUdonSharpDrawerFromRegistering()
        {
            // TODO: I'm not sure if this is still needed, or if this is the correct solution.
            // During normal operation, UdonSharp will draw an inspector that reflects the values and types of the actual UdonBehaviour.
            // We are trying to prevent this, because the UdonBehaviour state will not be in sync (fields/program variables might not match),
            // and it would have caused a lot of errors to be printed.
            
            var udonSharpToPatch = HackGetTypeByName("UdonSharpEditor.UdonBehaviourDrawerOverride");
            var theMethodThatIsCalledOnPlayMode = udonSharpToPatch.GetMethod("OverrideUdonBehaviourDrawer", BindingFlags.Static | BindingFlags.NonPublic);
            var ourPatch = typeof(MyrddinKillswitch).GetMethod(nameof(PatchCustomUdonSharpDrawer));
            
            DoPatch(theMethodThatIsCalledOnPlayMode, new HarmonyMethod(ourPatch));
        }

        private static void PreventUdonSharpFromCopyingUdonToProxy()
        {
            // During normal operation, UdonSharp will copy program variables from the UdonBehaviour to the UdonSharpBehaviour.
            // We need to prevent this, because it copies null values from the runtime UdonBehaviour.
            
            var udonSharpToPatch = HackGetTypeByName("UdonSharpEditor.UdonSharpEditorUtility");
            Type[] types = new Type[] { typeof(UdonSharpBehaviour), typeof(ProxySerializationPolicy) };
            var theMethodThatIsCalledOnPlayMode = udonSharpToPatch.GetMethod("CopyUdonToProxy", types);
            var ourPatch = typeof(MyrddinKillswitch).GetMethod(nameof(PatchCopyingUdonToProxy));
            
            DoPatch(theMethodThatIsCalledOnPlayMode, new HarmonyMethod(ourPatch));
        }
        
        private static void PreventVRChatEnvConfigFromSettingOculusLoaderAsTheXRPluginProvider()
        {
            // In VRCSDK base EnvConfig.cs, line 1102, this effectively mutes that one specific call to XRPackageMetadataStore.AssignLoader
            var classToPatch = HackGetTypeByName("UnityEditor.XR.Management.Metadata.XRPackageMetadataStore");
            var theMethodThatIsCalledOnPlayMode = classToPatch.GetMethod("AssignLoader");
            var ourPatch = typeof(MyrddinKillswitch).GetMethod(nameof(PatchAssignLoaderFromSettingOculusLoader));
            
            DoPatch(theMethodThatIsCalledOnPlayMode, new HarmonyMethod(ourPatch));
        }

        private static void RedirectUiEventsToUdonBehaviour()
        {
            var methodsToPatch = typeof(MyrddinKillswitch).GetMethods()
                .Where(info => info.Name.StartsWith(ReflectionPrefix))
                .ToArray();
            
            PatchThese(methodsToPatch, ReflectionPrefix, typeof(UdonBehaviour));
        }

        private static void ImplementDelaysInUdonSharpBehaviour()
        {
            var methodsToPatch = typeof(MyrddinKillswitch).GetMethods()
                .Where(info => info.Name.StartsWith(ImplementPrefix))
                .ToArray();
            
            PatchThese(methodsToPatch, ImplementPrefix, typeof(UdonSharpBehaviour));
        }

        // ReSharper disable InconsistentNaming
        // All methods here starting with __ (which is ReflectionPrefix) will be found through reflection and wired to the corresponding UdonBehaviour method.
        // The literal "__instance" name is required by Harmony, do not change it.
        [UsedImplicitly] public static bool __SendCustomEvent(UdonBehaviour __instance, string eventName) => RunSharp(__instance, sharp => sharp.SendCustomEvent(eventName));
        [UsedImplicitly] public static bool __SendCustomNetworkEvent(UdonBehaviour __instance, NetworkEventTarget target, string eventName) => RunSharp(__instance, sharp => sharp.SendCustomNetworkEvent(target, eventName));
        [UsedImplicitly] public static bool __SendCustomEventDelayedSeconds(UdonBehaviour __instance, string eventName, float delaySeconds, EventTiming eventTiming) => RunSharp(__instance, sharp =>
        {
            // TODO: Event timing is not implemented within the coroutine runner
            Debug.Log($"{__instance.name} is being sent custom event {eventName}, delayed by {delaySeconds} seconds (EVENT TIMING NOT IMPLEMENTED: {eventTiming})");
            MyrddinCoroutineRunner.CreateForSeconds(sharp, eventName, delaySeconds, eventTiming);
        });
        [UsedImplicitly] public static bool __SendCustomEventDelayedFrames(UdonBehaviour __instance, string eventName, int delayFrames, EventTiming eventTiming) => RunSharp(__instance, sharp =>
        {
            Debug.Log($"{__instance.name} is being sent custom event {eventName}, delayed by {delayFrames} frames (EVENT TIMING NOT IMPLEMENTED: {eventTiming})");
            MyrddinCoroutineRunner.CreateForFrames(sharp, eventName, delayFrames, eventTiming);
        });
        [UsedImplicitly] public static bool __Interact(UdonBehaviour __instance) => RunSharp(__instance, sharp => sharp.Interact());
        [UsedImplicitly] public static bool __OnPickup(UdonBehaviour __instance) => RunSharp(__instance, sharp => sharp.OnPickup());
        [UsedImplicitly] public static bool __OnDrop(UdonBehaviour __instance) => RunSharp(__instance, sharp => sharp.OnDrop());
        [UsedImplicitly] public static bool __OnPickupUseDown(UdonBehaviour __instance) => RunSharp(__instance, sharp => sharp.OnPickupUseDown());
        [UsedImplicitly] public static bool __OnPickupUseUp(UdonBehaviour __instance) => RunSharp(__instance, sharp => sharp.OnPickupUseUp());
        [UsedImplicitly] public static bool __OnEnable(UdonBehaviour __instance) => RunSharp(__instance, sharp =>
        {
            MyrddinPostLateUpdateExecutor.OnNewUdonSharpBehaviourIntroduced(sharp);
        });
        // ReSharper restore InconsistentNaming
        
        // ReSharper disable InconsistentNaming
        // All methods here starting with SharpFix__ (which is ImplementPrefix) will be found through reflection and wired to the corresponding UdonSharpBehaviour method.
        [UsedImplicitly] public static bool SharpFix__SendCustomEventDelayedSeconds(UdonSharpBehaviour __instance, string eventName, float delaySeconds, EventTiming eventTiming)
        {
            Debug.Log($"{__instance.name} is being sent custom event {eventName}, delayed by {delaySeconds} seconds (EVENT TIMING NOT IMPLEMENTED: {eventTiming})");
            MyrddinCoroutineRunner.CreateForSeconds(__instance, eventName, delaySeconds, eventTiming);
            return true;
        }
        [UsedImplicitly] public static bool SharpFix__SendCustomEventDelayedFrames(UdonSharpBehaviour __instance, string eventName, int delayFrames, EventTiming eventTiming)
        {
            Debug.Log($"{__instance.name} is being sent custom event {eventName}, delayed by {delayFrames} frames (EVENT TIMING NOT IMPLEMENTED: {eventTiming})");
            MyrddinCoroutineRunner.CreateForFrames(__instance, eventName, delayFrames, eventTiming);
            return true;
        }
        // ReSharper restore InconsistentNaming

        private static void PatchThese(MethodInfo[] ourPatches, string prefix, Type typeToPatch)
        {
            foreach (var ourPatch in ourPatches)
            {
                try
                {
                    DoPatch(typeToPatch.GetMethod(ourPatch.Name.Substring(prefix.Length), ourPatch.GetParameters().Select(info => info.ParameterType).Skip(1).ToArray()), new HarmonyMethod(ourPatch));
                }
                catch (Exception e)
                {
                    Debug.LogError($"(MyrddinKillswitch) Failed to patch {ourPatch}");
                    throw;
                }
            }
        }
        
        private static void DetectNewUdonSharpBehavioursThatNeedPostLateUpdate__ThroughConstructor()
        {
            // UNUSED: This partially works, but not on some objects when the scene starts. Not sure why.
            // Since it's in the constructor, it's also a pain because:
            // - the UdonSharp compiler will choke on the Harmony patch,
            // - you can't call anything from within the constructor to itself, so it's harder to get the object name and stuff.
            //
            // For now, see the reflective __OnEnable patch on UdonBehaviour
            
            var sharpTypes = AllTypesThatExtendUdonSharpBehaviour();
            var ourPatch = typeof(MyrddinKillswitch).GetMethod(nameof(PatchForLateUpdate));

            foreach (var sharpTypeToPatch in sharpTypes)
            {
                var constructorMethod = GetConstructor(sharpTypeToPatch);
                
                // This is a postfix patch.
                Harm.Patch(constructorMethod, postfix: new HarmonyMethod(ourPatch));
            }
        }

        private static void RemovePostLateUpdatePatch()
        {
            var sharpTypes = AllTypesThatExtendUdonSharpBehaviour();

            foreach (var sharpTypeToPatch in sharpTypes)
            {
                var constructorMethod = GetConstructor(sharpTypeToPatch);
                Harm.Unpatch(constructorMethod, HarmonyPatchType.Postfix, HarmonyIdentifier);
            }
        }

        private static MethodBase GetConstructor(Type sharpTypeToPatch)
        {
            return (MethodBase)sharpTypeToPatch.GetMember(".ctor", AccessTools.all)[0];
        }

        private static Type[] AllTypesThatExtendUdonSharpBehaviour()
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(assembly => assembly.GetTypes())
                .Where(t => typeof(UdonSharpBehaviour).IsAssignableFrom(t))
                .ToArray();
        }

        public static void PatchForLateUpdate(object __instance)
        {
            // Reminder: Due to a hack, we're in a constructor call. Don't invoke random stuff, like getting the name.
            
            MyrddinPostLateUpdateExecutor.OnNewUdonSharpBehaviourIntroduced((UdonSharpBehaviour)__instance);
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

        public static bool PatchExecutionMuteBehaviours()
        {
            Debug.Log("(MyrddinKillswitch) Prevented UdonSharpEditorManager from muting behaviours.");
            return false;
        }

        public static bool PatchExecutionPlayModeEntry()
        {
            Debug.Log("(MyrddinKillswitch) Prevented UdonSharpEditorManager from affecting play mode entry.");
            return false;
        }

        // FIXME: Probably not needed.
        public static bool PatchExecutionSceneBuild(bool isBuildingPlayer)
        {
            Debug.Log("(MyrddinKillswitch) Prevented UdonSharpEditorManager from doing operations on scene build.");
            return false;
        }

        // FIXME: Probably not needed.
        public static bool PatchExecutionCompilerPlayMode(PlayModeStateChange stateChange)
        {
            Debug.Log("(MyrddinKillswitch) Prevented UdonSharpCompilerV1 from doing operations on entering Play mode.");
            return false;
        }

        // FIXME: Probably no longer needed, it was the wrong issue (see PreventCopyingUdonToProxy).
        // Might still cause errors when fields are added or removed if this isn't running.
        public static bool PatchCustomUdonSharpDrawer()
        {
            Debug.Log("(MyrddinKillswitch) Prevented custom UdonSharp drawer from registering.");
            return false;
        }

        public static bool PatchCopyingUdonToProxy(UdonSharpBehaviour proxy, ProxySerializationPolicy serializationPolicy)
        {
            // This is called a lot, so do not log.
            // Debug.Log("(MyrddinKillswitch) Prevented copying Udon to Proxy.");
            
            return false;
        }

        public static bool PatchAssignLoaderFromSettingOculusLoader(XRManagerSettings settings, ref string loaderTypeName, BuildTargetGroup buildTargetGroup)
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

        private static bool RunSharp(UdonBehaviour __instance, Action<UdonSharpBehaviour> doFn)
        {
            if (MyrddinBehaviourCache.TryGetUdonSharpBehaviour(__instance, out var udonSharpBehaviour))
            {
                doFn.Invoke(udonSharpBehaviour);
                return false; // Reminder: This is a Harmony patching method. false prevents the original UdonBehaviour from executing
            }
            
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

            // Reminder that ClientSim does not need to be enabled. UdonManager is supposed to create itself no matter what.
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
            if (plu != null)
            {
                plu.enabled = false;
                
                // TODO: MyrddinPostLateUpdateExecutor requires a UdonBehaviour to exist. This may cause issues if UdonSharpBehaviours are instantiated directly?
                plu.gameObject.AddComponent<MyrddinPostLateUpdateExecutor>();
            }
        }
        
        private struct MemorizedPatch
        {
            internal MethodInfo Source;
            internal HarmonyMethod Destination;
        }
    }
}