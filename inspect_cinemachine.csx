#r "lib/Unity.Cinemachine.dll"
using System.Reflection;
var asm = Assembly.LoadFrom("lib/Unity.Cinemachine.dll");
var t = asm.GetType("Unity.Cinemachine.CinemachineOrbitalFollow");
if (t != null) {
    Console.WriteLine($"Type: {t.FullName}");
    foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance)) {
        Console.WriteLine($"  Prop: {p.PropertyType.Name} {p.Name}");
    }
}
