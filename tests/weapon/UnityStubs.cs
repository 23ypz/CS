using System;
using System.Collections.Generic;

// 仅替代引擎和网络边界，测试直接编译实际武器与协议源码。
namespace UnityEngine
{
    public class Object
    {
        public static int InstantiateCount;
        public static Object Instantiate(Object value, Vector3 position, Quaternion rotation)
        {
            InstantiateCount++;
            return value;
        }
        public static void Destroy(Object value, float delay) { }
    }

    public class MonoBehaviour : Object
    {
        private readonly Dictionary<Type, object> components = new Dictionary<Type, object>();
        public T GetComponent<T>() where T : class
        {
            object value;
            return components.TryGetValue(typeof(T), out value) ? (T)value : null;
        }
        public void Attach<T>(T component) where T : class { components[typeof(T)] = component; }
    }

    public class GameObject : Object { public Transform transform = new Transform(); }
    public class Transform { public Vector3 position, forward; public Quaternion rotation; }
    public struct Vector3 { public float x, y, z; }
    public struct Quaternion { }
    public class AudioClip : Object { }
    public class AudioSource
    {
        public int PlayCount;
        public void PlayOneShot(AudioClip clip) { PlayCount++; }
    }
    [AttributeUsage(AttributeTargets.Field)]
    public class HeaderAttribute : Attribute { public HeaderAttribute(string label) { } }
    [AttributeUsage(AttributeTargets.Field)]
    public class MinAttribute : Attribute { public MinAttribute(float minimum) { } }
    public enum KeyCode { R, Escape }
    public static class Input
    {
        public static bool R, Fire;
        public static bool GetKey(KeyCode key) { return key == KeyCode.R && R; }
        public static bool GetMouseButton(int button) { return button == 0 && Fire; }
    }
    public static class Time { public static float deltaTime, unscaledDeltaTime; }
    public static class Mathf
    {
        public static int Min(int a, int b) { return Math.Min(a, b); }
        public static float Min(float a, float b) { return Math.Min(a, b); }
        public static int Max(int a, int b) { return Math.Max(a, b); }
        public static float Max(float a, float b) { return Math.Max(a, b); }
        public static int Clamp(int value, int minimum, int maximum)
        { return Math.Min(maximum, Math.Max(minimum, value)); }
        public static float Clamp(float value, float minimum, float maximum)
        { return Math.Min(maximum, Math.Max(minimum, value)); }
        public static float Clamp01(float value) { return Clamp(value, 0f, 1f); }
    }
}

public class PlayerControl { public bool highSpeed; }
public class PlayerHealth { public bool IsDead; }
public class RecoilControl { public int FireCount; public void Fire() { FireCount++; } }
public static class GameModeManager { public static bool IsGameplayPaused, IsMenuVisible; }
public class NetworkMapData { }
public class NetworkClient
{
    public static NetworkClient Active;
    public bool IsGameStarted;
    public bool AcceptShots = true;
    public int ShootCalls;
    public readonly List<string> Actions = new List<string>();
    public bool SendShoot(UnityEngine.Vector3 origin, UnityEngine.Vector3 direction)
    {
        ShootCalls++;
        return AcceptShots;
    }
    public int SendAmmoAction(string action) { Actions.Add(action); return Actions.Count; }
}
