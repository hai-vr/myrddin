using System;
using UdonSharp;
using UnityEngine;

namespace Hai.Myrddin.Core.Runtime
{
    public class MyrddinHelpers : MonoBehaviour
    {
        public static Action<UdonSharpBehaviour> _stationFix = (Action<UdonSharpBehaviour>) null;
        
        public static void UseStation(UdonSharpBehaviour caller)
        {
            _stationFix(caller);
        }
    }
}