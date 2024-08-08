using UnityEngine;
using VRC.SDK3.ClientSim;
using VRC.Udon.Common;

namespace Hai.Myrddin.Core.Runtime
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
        private ClientSimInputBase _inputBase;

        public void ProvideInputBase(ClientSimInputBase inputBase)
        {
            _inputBase = inputBase;
        }

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
                // TODO: This shouldn't need to exist, because of ClientSimSystem's InputManager/ClientSimInputMapping (???)
                // Haven't figured it out.
                
                if (leftState.GripJustPressed) _inputBase.SendGrabEvent(true, HandType.LEFT);
                if (rightState.GripJustPressed) _inputBase.SendGrabEvent(true, HandType.RIGHT);
                
                if (leftState.GripJustReleased) _inputBase.SendGrabEvent(false, HandType.LEFT);
                if (rightState.GripJustReleased) _inputBase.SendGrabEvent(false, HandType.RIGHT);
                
                if (leftState.TriggerJustPressed) _inputBase.SendUseEvent(true, HandType.LEFT);
                if (rightState.TriggerJustPressed) _inputBase.SendUseEvent(true, HandType.RIGHT);
                
                if (leftState.TriggerJustReleased) _inputBase.SendUseEvent(false, HandType.LEFT);
                if (rightState.TriggerJustReleased) _inputBase.SendUseEvent(false, HandType.RIGHT);
            }
            
            // TODO: Provide for Humanoid data? We need a small IK solver here.
            
            // TODO: We need PostLateUpdate support, which was removed from UdonManager.
        }

        private void CopyCameraSpaceToAvatarSpace()
        {
            CopyTransform(cameraSpace, avatarSpace);
            CopyTransformLocal(viewpointRepresentation, avatarSpaceViewpoint);
            CopyTransformLocal(leftController, avatarSpaceLeft);
            CopyTransformLocal(rightController, avatarSpaceRight);
        }

        private void CopyTransform(Transform from, Transform to)
        {
            to.transform.position = from.transform.position;
            to.transform.rotation = from.transform.rotation;
            to.transform.localScale = from.transform.localScale;
        }

        private void CopyTransformLocal(Transform from, Transform to)
        {
            to.transform.localPosition = from.transform.localPosition;
            to.transform.localRotation = from.transform.localRotation;
            to.transform.localScale = from.transform.localScale;
        }
    }
}