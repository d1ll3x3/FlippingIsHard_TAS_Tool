using System;
using System.Reflection;

class InspectDll {
    static void Main() {
        var asm = Assembly.LoadFrom("lib/Unity.Cinemachine.dll");
        var t = asm.GetType("Unity.Cinemachine.CinemachineOrbitalFollow");
        if (t != null) {
            Console.WriteLine($"Type: {t.FullName}");
            Console.WriteLine("Properties:");
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance)) {
                Console.WriteLine($"  {p.PropertyType.Name} {p.Name}");
            }
            Console.WriteLine("Fields:");
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance)) {
                Console.WriteLine($"  {f.FieldType.Name} {f.Name}");
            }
        } else {
            Console.WriteLine("CinemachineOrbitalFollow not found");
        }
    }
}
