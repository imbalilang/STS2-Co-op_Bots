using System.Reflection;
using System.Runtime.Loader;

// The game's mod loader enumerates every type in CoopBots.dll *before* it calls
// our initializer, and only the initializer loads CoopBots.Kernel. Any type that
// needs the kernel while it is being loaded fails the whole mod with
// ReflectionTypeLoadException. The trigger is narrow but easy to reintroduce: a
// value type with a kernel-typed field, such as an iterator or async state
// machine that captured a ValueTuple<CardModel, KernelSession, int>. Reference
// fields carry no such layout requirement, so a plain record is safe.
//
// This runs as its own process on purpose: its application directory does not
// contain CoopBots.Kernel.dll, so refusing the kernel by name is decisive. An
// in-process check would still see the kernel loaded by the test harness (or
// found in its probing path) and prove nothing.
//
// Usage: TypeLoadCheck <dir containing CoopBots.dll> [extra probe dirs...]
if (args.Length < 1)
{
    Console.Error.WriteLine("usage: TypeLoadCheck <dir with CoopBots.dll> [probe dirs...]");
    return 2;
}

var target = Path.Combine(args[0], "CoopBots.dll");
if (!File.Exists(target))
{
    Console.Error.WriteLine($"CoopBots.dll not found: {target}");
    return 2;
}

var probes = new List<string> { args[0] };
probes.AddRange(args.Skip(1));
var context = new AssemblyLoadContext("CoopBotsTypeLoadGuard", isCollectible: true);
context.Resolving += (_, name) =>
{
    if (name.Name == "CoopBots.Kernel") return null;
    foreach (var dir in probes)
    {
        var candidate = Path.Combine(dir, name.Name + ".dll");
        if (File.Exists(candidate)) return context.LoadFromAssemblyPath(candidate);
    }
    return null;
};
try
{
    var kernelReachable = true;
    try { context.LoadFromAssemblyName(new AssemblyName("CoopBots.Kernel")); }
    catch { kernelReachable = false; }
    if (kernelReachable)
    {
        Console.Error.WriteLine("guard is not isolated: CoopBots.Kernel resolved from the probing paths");
        return 2;
    }

    int count;
    try
    {
        count = context.LoadFromAssemblyPath(target).GetTypes().Length;
    }
    catch (ReflectionTypeLoadException error)
    {
        var detail = error.LoaderExceptions.FirstOrDefault()?.Message ?? "unknown";
        Console.Error.WriteLine("CoopBots.dll cannot be enumerated without CoopBots.Kernel; "
            + "the mod will fail to load. A type has a kernel-typed field it resolves at load time.");
        Console.Error.WriteLine(detail);
        return 1;
    }
    if (count == 0)
    {
        Console.Error.WriteLine("CoopBots.dll reported no types.");
        return 1;
    }
    Console.WriteLine($"CoopBots.dll enumerates {count} types with CoopBots.Kernel unresolvable.");
    return 0;
}
finally
{
    context.Unload();
}
