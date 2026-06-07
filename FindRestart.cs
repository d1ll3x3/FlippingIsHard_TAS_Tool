using System;
using System.Reflection;
using System.Linq;

class Program {
    static void Main() {
        var asm = Assembly.LoadFrom(@"D:\TAS tool\lib\Assembly-CSharp.dll");
        foreach (var type in asm.GetTypes()) {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)) {
                if (method.Name.IndexOf("Restart", StringComparison.OrdinalIgnoreCase) >= 0) {
                    Console.WriteLine(type.FullName + "." + method.Name);
                }
            }
        }
    }
}
