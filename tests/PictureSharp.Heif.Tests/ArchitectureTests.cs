using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security;

namespace PictureSharp.Formats.Heif.Tests;

public sealed class ArchitectureTests
{
    private static readonly Assembly Plugin = typeof(HeifFormat).Assembly;

    [Test]
    public async Task Plugin_ReferencesOnlyPictureSharp_AndTheRuntime()
    {
        string[] references = [.. Plugin.GetReferencedAssemblies().Select(a => a.Name!)];
        await Assert.That(references.Where(name => name != "PictureSharp" && !IsRuntime(name))).IsEmpty();
        await Assert.That(references).Contains("PictureSharp");
        await Assert.That(Plugin.GetCustomAttributes<InternalsVisibleToAttribute>()).IsEmpty();
    }

    [Test]
    public async Task PictureSharp_DoesNotExposeItsInternalsToThePlugin()
    {
        // Without InternalsVisibleTo the compiler only lets the plugin use the public API of the package.
        string[] friends = [.. typeof(Image).Assembly.GetCustomAttributes<InternalsVisibleToAttribute>().Select(a => a.AssemblyName)];
        await Assert.That(friends.Where(name => name.StartsWith("PictureSharp.Heif", StringComparison.Ordinal))).IsEmpty();
    }

    [Test]
    public async Task Plugin_ExportsTypesOnlyFromItsFormatNamespace()
    {
        foreach (Type type in Plugin.GetExportedTypes())
        {
            await Assert.That(type.Namespace).IsEqualTo("PictureSharp.Formats.Heif");
        }
    }

    [Test]
    public async Task Plugin_HasNoUnsafeCodeOrPInvoke()
    {
        // AllowUnsafeBlocks marks the module as unverifiable; P/Invoke appears as DllImport/LibraryImport methods.
        await Assert.That(Plugin.ManifestModule.GetCustomAttributes<UnverifiableCodeAttribute>()).IsEmpty();
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        string[] pinvoke = [.. Plugin.GetTypes().SelectMany(t => t.GetMethods(all))
            .Where(m => (m.Attributes & MethodAttributes.PinvokeImpl) != 0)
            .Select(m => $"{m.DeclaringType}.{m.Name}")];
        await Assert.That(pinvoke).IsEmpty();
    }

    private static bool IsRuntime(string name) =>
        File.Exists(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), name + ".dll"));
}
