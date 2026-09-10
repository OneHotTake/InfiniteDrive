using System.Reflection;
using System.Runtime.Loader;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: AbiInspector ASSEMBLY TYPE");
    return 2;
}

var directory = Path.GetDirectoryName(Path.GetFullPath(args[0]))!;
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    var dependency = Path.Combine(directory, name.Name + ".dll");
    return File.Exists(dependency) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(dependency) : null;
};

var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(args[0]));
var type = assembly.GetType(args[1], throwOnError: true)!;
foreach (var method in type.GetMethods())
{
    var parameters = string.Join(", ", method.GetParameters().Select(p => $"{p.ParameterType.FullName} {p.Name}"));
    Console.WriteLine($"{method.ReturnType.FullName} {method.Name}({parameters})");
}

return 0;
