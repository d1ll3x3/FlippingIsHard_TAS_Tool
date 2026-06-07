using System;
using System.Reflection;

class Program
{
    static void Main()
    {
        try
        {
            var asm = Assembly.LoadFrom(@"I:\SteamLibrary\steamapps\common\Flipping is Hard Demo\BepInEx\interop\Unity.Cinemachine.dll");
            foreach (var type in asm.GetTypes())
            {
                if (type.Name.Contains("CinemachineBrain") || type.Name.Contains("CinemachineCore") || type.Name.Contains("Axis"))
                {
                    Console.WriteLine(type.FullName);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.ToString());
        }
    }
}
