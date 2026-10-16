using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Only the disposable workspace copy is modified. No game assembly is loaded/executed.
if (args.Length < 2 || args.Length > 3) throw new ArgumentException("Usage: SandboxPatcher <workspace> <save-directory> [a|b|c]");
var workspace = Path.GetFullPath(args[0]);
var instance = args.Length == 3 ? args[2] : "a";
if (instance != "a" && instance != "b" && instance != "c") throw new ArgumentException("Instance must be a, b or c.");
var game = Path.Combine(workspace, instance == "a" ? "test-game" : "test-game-" + instance);
var prefsPrefix = "FateCoopSandbox" + instance.ToUpperInvariant() + ":";
var managed = Path.Combine(game, "FateReawakened_Data", "Managed");
var saves = Path.GetFullPath(args[1]);
if (!saves.StartsWith(workspace + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Test saves must be inside the development workspace.");
Directory.CreateDirectory(saves);
var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(managed);
var report = new List<object>();
foreach (var name in new[] { "Assembly-CSharp.dll", "Assembly-CSharp-firstpass.dll" })
{
    var file = Path.Combine(managed, name);
    var backup = Path.Combine(workspace, "artifacts", name + ".original");
    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
    if (!File.Exists(backup)) File.Copy(file, backup);
    // Rebuild from the pristine workspace backup, making this operation repeatable.
    using var assembly = AssemblyDefinition.ReadAssembly(backup, new ReaderParameters { AssemblyResolver = resolver });
    var module = assembly.MainModule;
    var methods = AllTypes(module.Types).SelectMany(t => t.Methods).Where(m => m.HasBody).ToArray();
    var wrapperType = new TypeDefinition("FateCoop", "SandboxPrefs", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed, module.TypeSystem.Object);
    var wrappers = new Dictionary<string, MethodDefinition>();
    module.Types.Add(wrapperType);
    var persistentCount = 0;
    var prefsCount = 0;
    foreach (var method in methods)
    {
        foreach (var instruction in method.Body.Instructions)
        {
            if (instruction.Operand is not MethodReference reference) continue;
            if (reference.DeclaringType.FullName == "UnityEngine.Application" && reference.Name == "get_persistentDataPath")
            {
                instruction.OpCode = OpCodes.Ldstr;
                instruction.Operand = saves;
                persistentCount++;
            }
            else if (reference.DeclaringType.FullName == "UnityEngine.PlayerPrefs")
            {
                if (!wrappers.TryGetValue(reference.FullName, out var wrapper))
                {
                    wrapper = new MethodDefinition(reference.Name + "_" + wrappers.Count, MethodAttributes.Public | MethodAttributes.Static, module.ImportReference(reference.ReturnType));
                    foreach (var parameter in reference.Parameters)
                        wrapper.Parameters.Add(new ParameterDefinition(parameter.Name, ParameterAttributes.None, module.ImportReference(parameter.ParameterType)));
                    var il = wrapper.Body.GetILProcessor();
                    // Never clear or flush the user's entire native PlayerPrefs store.
                    if (reference.Name is not "DeleteAll" and not "Save")
                    {
                        if (wrapper.Parameters.Count == 0 || wrapper.Parameters[0].ParameterType.MetadataType != MetadataType.String)
                            throw new InvalidOperationException("Unexpected PlayerPrefs signature: " + reference.FullName);
                        il.Emit(OpCodes.Ldstr, prefsPrefix);
                        il.Emit(OpCodes.Ldarg_0);
                        var concat = new MethodReference("Concat", module.TypeSystem.String, module.TypeSystem.String) { HasThis = false };
                        concat.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
                        concat.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
                        il.Emit(OpCodes.Call, concat);
                        for (var n = 1; n < wrapper.Parameters.Count; n++) il.Emit(OpCodes.Ldarg, wrapper.Parameters[n]);
                        il.Emit(OpCodes.Call, module.ImportReference(reference));
                    }
                    il.Emit(OpCodes.Ret);
                    wrapperType.Methods.Add(wrapper);
                    wrappers.Add(reference.FullName, wrapper);
                }
                instruction.Operand = wrapper;
                prefsCount++;
            }
        }
    }
    if (name == "Assembly-CSharp.dll" && persistentCount == 0)
        throw new InvalidOperationException("Expected save patch points missing; refusing deployment.");
    var staged = file + ".sandbox";
    assembly.Write(staged);
    using (var verify = AssemblyDefinition.ReadAssembly(staged))
    {
        var refs = AllTypes(verify.MainModule.Types).SelectMany(t => t.Methods).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions);
        if (refs.Any(i => i.Operand is MethodReference m && m.DeclaringType.FullName == "UnityEngine.Application" && m.Name == "get_persistentDataPath"))
            throw new InvalidOperationException("Unredirected persistent path remains.");
        var getPath = AllTypes(verify.MainModule.Types).FirstOrDefault(t => t.FullName == "Tivola.Framework.SaveManagerStandalone")?.Methods.Single(m => m.Name == "GetFilePath");
        if (name == "Assembly-CSharp.dll" && !(getPath?.Body.Instructions.Any(i => Equals(i.Operand, saves)) ?? false))
            throw new InvalidOperationException("Save backend did not receive the isolated path.");
    }
    File.Move(staged, file, true);
    report.Add(new { assembly = name, persistentCount, prefsCount, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))) });
}
var manifest = JsonSerializer.Serialize(new { saveDirectory = saves, playerPrefsPrefix = prefsPrefix, assemblies = report }, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText(Path.Combine(game, "fate-coop-sandbox.json"), manifest);
Console.WriteLine(manifest);

static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
{
    foreach (var type in types)
    {
        yield return type;
        foreach (var nested in AllTypes(type.NestedTypes)) yield return nested;
    }
}
