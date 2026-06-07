using System;
using UnityEngine;
using Unity.Cinemachine;

public class TestScript {
    public static void LogComponents(CinemachineCamera cinCam) {
        foreach (var comp in cinCam.GetComponents<CinemachineComponentBase>()) {
            Console.WriteLine(comp.GetType().Name);
        }
    }
}
