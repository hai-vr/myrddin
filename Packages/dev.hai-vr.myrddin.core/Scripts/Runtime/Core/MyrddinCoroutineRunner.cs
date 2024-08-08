using System.Collections;
using UdonSharp;
using UnityEngine;
using VRC.Udon.Common.Enums;

namespace Hai.Myrddin.Core.Runtime
{
    public class MyrddinCoroutineRunner : MonoBehaviour
    {
        private UdonSharpBehaviour _sharp;

        public static void CreateForSeconds(UdonSharpBehaviour sharp, string eventName, float delaySeconds, EventTiming eventTiming)
        {
            // TODO: Event timing is not implemented.
            
            var runner = CreateTemporaryObject(sharp, eventName);
            runner.StartCoroutine(runner.Runner(runner.DelaySeconds(eventName, delaySeconds), eventName));
        }

        public static void CreateForFrames(UdonSharpBehaviour sharp, string eventName, int delayFrames, EventTiming eventTiming)
        {
            // TODO: Event timing is not implemented.
            
            var runner = CreateTemporaryObject(sharp, eventName);
            runner.StartCoroutine(runner.Runner(runner.DelayFrames(eventName, delayFrames), eventName));
        }

        private static MyrddinCoroutineRunner CreateTemporaryObject(UdonSharpBehaviour sharp, string eventName)
        {
            // Since SendCustomEventDelayed* functions can work on disabled UdonBehaviours, spawn an independent coroutine to handle the delay for us.
            var runner = new GameObject
            {
                name = $"Coroutine({sharp.name}.{eventName})"
            }.AddComponent<MyrddinCoroutineRunner>();
            Object.DontDestroyOnLoad(runner);
            runner._sharp = sharp;
            return runner;
        }

        private IEnumerator Runner(IEnumerator enumerator, string eventName)
        {
            yield return enumerator;
            Debug.Log($"(MyrddinCoroutineRunner) Temporary runner complete for {eventName}.");
            Object.Destroy(this.gameObject);
        }

        private IEnumerator DelaySeconds(string eventName, float delaySeconds)
        {    
            yield return new WaitForSeconds(delaySeconds);
            TriggerEvent(eventName);
        }
        
        private IEnumerator DelayFrames(string eventName, float delayFrameCount)
        {
            // FIXME: Is this correct???
            if (delayFrameCount <= 0) delayFrameCount = 1;
            
            while (delayFrameCount > 0)
            {
                yield return new WaitForSeconds(0f);
                delayFrameCount--;
            }
            
            TriggerEvent(eventName);
        }

        private void TriggerEvent(string eventName)
        {
            if (_sharp == null) return;
            
            // For parity, does not interrupt execution on the caller if there's no match.
            var method = _sharp.GetType().GetMethod(eventName);
            if (method == null) return;
            
            Debug.Log($"(MyrddinCoroutineRunner) Invoking event {eventName} on {_sharp.name} ({_sharp.GetType().Name})");
            method.Invoke(_sharp, null);
        }
    }
}