using UnityEngine;
using VRC.SDK3.ClientSim;
using VRC.Udon.Common;

namespace Hai.Myrddin.Runtime
{
    public class MyrddinUpdateCoordinator : MonoBehaviour
    {
        public Transform viewpointRepresentation;
        public Transform leftController;
        public Transform rightController;
        
        public Transform cameraSpace;
        public Transform avatarSpace;
        public Transform avatarSpaceViewpoint;
        public Transform avatarSpaceLeft;
        public Transform avatarSpaceRight;
        
        public MyrddinController leftState;
        public MyrddinController rightState;

        private const bool UseClientSim = true; // FIXME: Detect ClientSim usage if we want to bypass it.
        private ClientSimInputBase inputBase;

        private void Update()
        {
            CopyCameraSpaceToAvatarSpace();

            leftState.DoUpdate();
            rightState.DoUpdate();

            MyrddinInput.LeftTrigger = leftState.TriggerAnalog;
            MyrddinInput.LeftGrip = leftState.GripAnalog;
            MyrddinInput.LeftAxisX = leftState.Thumbstick.x;
            MyrddinInput.LeftAxisY = leftState.Thumbstick.y;
            
            MyrddinInput.RightTrigger = rightState.TriggerAnalog;
            MyrddinInput.RightGrip = rightState.GripAnalog;
            MyrddinInput.RightAxisX = rightState.Thumbstick.x;
            MyrddinInput.RightAxisY = rightState.Thumbstick.y;

            if (UseClientSim)
            {
                var clientSimInput = FindInputBase();
                
                if (leftState.GripJustPressed) clientSimInput.SendGrabEvent(true, HandType.LEFT);
                if (rightState.GripJustPressed) clientSimInput.SendGrabEvent(true, HandType.RIGHT);
                
                if (leftState.GripJustReleased) clientSimInput.SendGrabEvent(false, HandType.LEFT);
                if (rightState.GripJustReleased) clientSimInput.SendGrabEvent(false, HandType.RIGHT);
                
                if (leftState.TriggerJustPressed) clientSimInput.SendUseEvent(true, HandType.LEFT);
                if (rightState.TriggerJustPressed) clientSimInput.SendUseEvent(true, HandType.RIGHT);
                
                if (leftState.TriggerJustReleased) clientSimInput.SendUseEvent(false, HandType.LEFT);
                if (rightState.TriggerJustReleased) clientSimInput.SendUseEvent(false, HandType.RIGHT);
            }
            
            // TODO: Provide for Humanoid data? We need a small IK solver here.
            
            // TODO: We need PostLateUpdate support, which was removed from UdonManager.
        }

        private void CopyCameraSpaceToAvatarSpace()
        {
            CopyTransform(cameraSpace, avatarSpace);
            CopyTransform(viewpointRepresentation, avatarSpaceViewpoint);
            CopyTransform(leftController, avatarSpaceLeft);
            CopyTransform(rightController, avatarSpaceRight);
        }

        private void CopyTransform(Transform from, Transform to)
        {
            to.transform.position = from.transform.position;
            to.transform.rotation = from.transform.rotation;
            to.transform.localScale = from.transform.localScale;
        }

        private ClientSimInputBase FindInputBase()
        {
            // FIXME: Figure out a way to inject this
            if (inputBase == null)
            {
                var temp = new GameObject();
                DontDestroyOnLoad(temp);
                var rootGos = temp.scene.GetRootGameObjects();
                Destroy(temp);
                
                foreach (var rootGo in rootGos)
                {
                    var inputManager = rootGo.GetComponentInChildren<ClientSimInputManager>(true);
                    if (inputManager != null)
                    {
                        inputBase = inputManager.GetInput() as ClientSimInputBase;
                        return inputBase;
                    }
                }
                Debug.Log("(MyrddinUpdateCoordinator) Failed to get ClientSimInputBase. Is ClientSim running?");
            }

            return inputBase;
        }
    }
}