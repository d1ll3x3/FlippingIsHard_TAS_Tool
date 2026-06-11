using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace FlippingIsHardTAS
{
    public static class OSInput
    {
        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern uint MapVirtualKey(uint uCode, uint uMapType);

        public const uint INPUT_KEYBOARD = 1;
        public const uint KEYEVENTF_KEYUP = 0x0002;
        public const uint KEYEVENTF_SCANCODE = 0x0008;

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public uint type;
            public InputUnion U;
            public static int Size => Marshal.SizeOf(typeof(INPUT));
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion
        {
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        public static bool IsKeyDown(KeyCode key)
        {
            int vKey = GetVirtualKeyCode(key);
            if (vKey == 0) return false;
            return (GetAsyncKeyState(vKey) & 0x8000) != 0;
        }

        public static void SetKeyPressed(KeyCode key, bool pressed)
        {
            int vKey = GetVirtualKeyCode(key);
            if (vKey == 0) return;

            ushort scanCode = (ushort)MapVirtualKey((uint)vKey, 0); // MAPVK_VK_TO_VSC

            INPUT[] inputs = new INPUT[1];
            inputs[0].type = INPUT_KEYBOARD;
            inputs[0].U.ki.wVk = 0; // Use 0 when using scancodes for raw input
            inputs[0].U.ki.wScan = scanCode;
            inputs[0].U.ki.dwFlags = KEYEVENTF_SCANCODE;

            if (!pressed)
            {
                inputs[0].U.ki.dwFlags |= KEYEVENTF_KEYUP;
            }

            // For extended keys like arrows, we need the extended flag
            if (key == KeyCode.UpArrow || key == KeyCode.DownArrow || 
                key == KeyCode.LeftArrow || key == KeyCode.RightArrow)
            {
                inputs[0].U.ki.dwFlags |= 0x0001; // KEYEVENTF_EXTENDEDKEY
            }

            SendInput(1, inputs, INPUT.Size);
        }

        public static int GetVirtualKeyCode(KeyCode key)
        {
            switch (key)
            {
                case KeyCode.W: return 0x57;
                case KeyCode.A: return 0x41;
                case KeyCode.S: return 0x53;
                case KeyCode.D: return 0x44;
                case KeyCode.Space: return 0x20;
                case KeyCode.E: return 0x45;
                case KeyCode.LeftShift: return 0xA0;
                case KeyCode.UpArrow: return 0x26;
                case KeyCode.DownArrow: return 0x28;
                case KeyCode.LeftArrow: return 0x25;
                case KeyCode.RightArrow: return 0x27;
                default: return 0;
            }
        }
    }
}
