using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: CecilInjector <merged-input.dll> <output.dll>");
            return 2;
        }

        string input = Path.GetFullPath(args[0]);
        string output = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);

        var rp = new ReaderParameters { ReadSymbols = false, InMemory = true };
        using var asm = AssemblyDefinition.ReadAssembly(input, rp);
        var module = asm.MainModule;

        var camera = module.Types.FirstOrDefault(t => t.FullName == "CameraRework.CameraReworkMod");
        if (camera == null) throw new InvalidOperationException("CameraRework.CameraReworkMod was not found after merge.");

        var aar = module.Types.FirstOrDefault(t => t.FullName == "CameraRework.LiveAAR.LiveAarBootstrap");
        if (aar == null) throw new InvalidOperationException("LiveAarBootstrap was not found after merge.");

        Inject(camera, "OnInitializeMelon", GetStatic(aar, "EnsureInitialized"));
        Inject(camera, "OnLateUpdate", GetStatic(aar, "Tick"));
        Inject(camera, "OnGUI", GetStatic(aar, "Draw"));
        Inject(camera, "OnSceneWasLoaded", GetStatic(aar, "SceneLoaded"), optional: true);
        Inject(camera, "OnSceneWasUnloaded", GetStatic(aar, "SceneUnloaded"), optional: true);

        var helperRef = module.AssemblyReferences.FirstOrDefault(r => r.Name == "CameraRework.AAR");
        if (helperRef != null)
            throw new InvalidOperationException("Merge incomplete: CameraRework.AAR remains as an external AssemblyRef.");

        asm.Write(output, new WriterParameters { WriteSymbols = false });

        Console.WriteLine("Injected Live AAR lifecycle calls into Camera Rework.");
        Console.WriteLine("Output: " + output);
        Console.WriteLine("Size: " + new FileInfo(output).Length + " bytes");
        return 0;
    }

    private static MethodDefinition GetStatic(TypeDefinition type, string name)
    {
        var method = type.Methods.FirstOrDefault(m => m.Name == name && m.IsStatic && !m.HasParameters);
        if (method == null) throw new InvalidOperationException(type.FullName + "." + name + "() not found.");
        return method;
    }

    private static void Inject(TypeDefinition type, string targetName, MethodDefinition call, bool optional = false)
    {
        var target = type.Methods.FirstOrDefault(m => m.Name == targetName && m.HasBody);
        if (target == null)
        {
            if (optional)
            {
                Console.WriteLine("Optional target not present: " + type.FullName + "." + targetName);
                return;
            }
            throw new InvalidOperationException(type.FullName + "." + targetName + " was not found.");
        }

        bool exists = target.Body.Instructions.Any(i =>
            i.OpCode == OpCodes.Call && i.Operand is MethodReference mr && mr.FullName == call.FullName);
        if (exists)
        {
            Console.WriteLine("Already injected: " + targetName + " -> " + call.Name);
            return;
        }

        var il = target.Body.GetILProcessor();
        var first = target.Body.Instructions.First();
        il.InsertBefore(first, il.Create(OpCodes.Call, call));
        Console.WriteLine("Injected: " + targetName + " -> " + call.Name);
    }
}
