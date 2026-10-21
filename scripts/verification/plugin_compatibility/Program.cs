using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

// Failure cases: an installed host can reject a newer SDK reference even when
// minHostVersion passes; a present DLL can lack a loadable entry type. Exercise
// the actual host loader, without activation, audio capture, or user-data writes.
if (args.Length is < 3 or > 4)
{
    Console.Error.WriteLine("Usage: CompatibilityProbe HOST_DIRECTORY PLUGINS_DIRECTORY PLUGIN_ID_OR_* [EXPECTED_HOST_VERSION]");
    return 2;
}

var hostDirectory = Path.GetFullPath(args[0]);
var pluginDirectory = Path.GetFullPath(args[1]);
var expectedId = args[2];
var hostPath = Path.Join(hostDirectory, "typewhisper.dll");
var resolver = new AssemblyDependencyResolver(hostPath);
AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    var path = resolver.ResolveAssemblyToPath(name);
    // Do not let this diagnostic resolver bypass the runtime's version check.
    // A normal app does not register a handler that supplies an older contract.
    if (path is null || AssemblyName.GetAssemblyName(path).Version < name.Version)
        return null;
    return context.LoadFromAssemblyPath(path);
};
Trace.Listeners.Add(new TextWriterTraceListener(Console.Error));
Trace.AutoFlush = true;
var host = AssemblyLoadContext.Default.LoadFromAssemblyPath(hostPath);
var sdk = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Join(hostDirectory, "TypeWhisper.PluginSDK.dll"));
var loaderType = host.GetType("TypeWhisper.Linux.Services.Plugins.PluginLoader", throwOnError: true)!;
var scratch = Path.Join(Path.GetTempPath(), "tw-needle-probe-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
try
{
    var constructor = loaderType.GetConstructor([typeof(string)]);
    var loader = constructor is null ? Activator.CreateInstance(loaderType)! : constructor.Invoke([scratch]);
    var plugins = (System.Collections.IEnumerable)loaderType.GetMethod("DiscoverAndLoad")!
        .Invoke(loader, [new[] { pluginDirectory }])!;
    var ids = new List<string>();
    foreach (var plugin in plugins)
    {
        var manifest = plugin!.GetType().GetProperty("Manifest")!.GetValue(plugin)!;
        ids.Add((string)manifest.GetType().GetProperty("Id")!.GetValue(manifest)!);
        ((IDisposable)plugin.GetType().GetProperty("Instance")!.GetValue(plugin)!).Dispose();
        ((AssemblyLoadContext)plugin.GetType().GetProperty("LoadContext")!.GetValue(plugin)!).Unload();
    }
    var failures = loaderType.GetProperty("LastLoadFailures")?.GetValue(loader);
    var appVersion = host.GetType("TypeWhisper.Linux.Services.AppVersion", true)!
        .GetProperty("Display")!.GetValue(null)?.ToString();
    var expectedIds = Directory.EnumerateDirectories(pluginDirectory)
        .Select(directory => Path.Join(directory, "manifest.json"))
        .Where(File.Exists)
        .Select(path =>
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(path));
            return manifest.RootElement.GetProperty("id").GetString()!;
        }).ToArray();
    var success = expectedId == "*"
        ? expectedIds.Length > 0 && expectedIds.All(ids.Contains)
          && !(failures as System.Collections.IEnumerable ?? Array.Empty<object>()).Cast<object>().Any()
        : ids.Contains(expectedId);
    success &= args.Length < 4 || appVersion == args[3];
    string? typeLoadFailure = null;
    if (!success && expectedId != "*" && Directory.Exists(Path.Join(pluginDirectory, expectedId)))
    {
        var directory = Path.Join(pluginDirectory, expectedId);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Join(directory, "manifest.json")));
        var assemblyPath = Path.Join(directory, manifest.RootElement.GetProperty("assemblyName").GetString()!);
        var contextType = host.GetType("TypeWhisper.Linux.Services.Plugins.PluginAssemblyLoadContext", true)!;
        var context = (AssemblyLoadContext)Activator.CreateInstance(contextType, assemblyPath)!;
        try
        {
            context.LoadFromAssemblyPath(assemblyPath).GetType(
                manifest.RootElement.GetProperty("pluginClass").GetString()!, throwOnError: true);
        }
        catch (Exception error)
        {
            typeLoadFailure = error.ToString();
        }
        finally
        {
            context.Unload();
        }
    }
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        host = host.GetName().Version?.ToString(),
        app_version = appVersion,
        sdk = sdk.GetName().Version?.ToString(),
        expected_plugin = expectedId,
        loaded = ids,
        failures,
        type_load_failure = typeLoadFailure,
        success,
    }, new JsonSerializerOptions { WriteIndented = true }));
    return success ? 0 : 1;
}
finally
{
    Directory.Delete(scratch, recursive: true);
}
