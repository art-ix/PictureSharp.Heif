using System.Globalization;
using System.Text.RegularExpressions;

namespace PictureSharp.Formats.Heif.Tests;

internal sealed record PortraitExport(string Path, string Variant, float Aperture, bool Refocus);
internal sealed record PortraitScene(string Name, string Original, bool Edited, bool ObjectScene, PortraitExport[] Exports);
internal sealed record PortraitCatalog(PortraitScene[] Scenes, string[] Ignored, string[] Notices);

internal static partial class PortraitSampleCatalog
{
    [GeneratedRegex(@"^IMG_\d{4}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OriginalName();

    [GeneratedRegex(@"^(IMG_\d{4})_(?:(refocus(?:_[a-z0-9]+)?)_)?a(\d+(?:\.\d+)?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExportName();

    // Test shots of one branch taken in poor light. By the user's decision (2026-10-19) they are reported as a separate
    // cohort, like edited scenes, outside the clean aggregates; every pair still passes the per-pair regression gate.
    public static bool IsLowLightTest(string scene) => scene is "liscie_1x" or "liscie_2x" or "liscie_4x";

    public static PortraitCatalog Scan(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return new([], [], []);
        }

        var scenes = new List<PortraitScene>();
        var ignored = new List<string>();
        var notices = new List<string>();
        foreach (string folder in Directory.GetDirectories(directory).Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(folder);
            string[] files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
            string[] heic = files.Where(p => Path.GetExtension(p).Equals(".heic", StringComparison.OrdinalIgnoreCase)).ToArray();
            ignored.AddRange(files.Except(heic).Select(p => Path.GetRelativePath(directory, p)));
            string[] originals = heic.Where(p => OriginalName().IsMatch(Path.GetFileNameWithoutExtension(p))).ToArray();
            bool legacy = name == "portret_4x" && originals.Length == 2
                && originals.Any(p => Path.GetFileName(p) == "IMG_8083.HEIC")
                && originals.Any(p => Path.GetFileName(p) == "IMG_8083.heic");
            if (originals.Length != 1 && !legacy)
            {
                throw new InvalidDataException($"{name}: expected exactly one original, found {originals.Length}; resolve ambiguous names explicitly.");
            }

            string original = legacy ? originals.Single(p => Path.GetFileName(p) == "IMG_8083.HEIC") : originals[0];
            string stem = Path.GetFileNameWithoutExtension(original);
            var exports = new List<PortraitExport>();
            foreach (string file in heic.Where(p => p != original))
            {
                if (legacy && Path.GetFileName(file) == "IMG_8083.heic")
                {
                    exports.Add(new(file, "e1", 4.5f, false));
                    notices.Add("portret_4x: explicit case-sensitive mapping IMG_8083.HEIC -> original, IMG_8083.heic -> edited e1, f/4.5.");
                    continue;
                }

                Match match = ExportName().Match(Path.GetFileNameWithoutExtension(file));
                if (!match.Success || !match.Groups[1].Value.Equals(stem, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"{name}: unclassified HEIC {Path.GetFileName(file)}.");
                }

                float aperture = float.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
                if (aperture is < 1.4f or > 16f)
                {
                    throw new InvalidDataException($"{name}: invalid aperture in {Path.GetFileName(file)}.");
                }

                string variant = match.Groups[2].Success ? match.Groups[2].Value.ToLowerInvariant() : "default";
                if (exports.Any(e => e.Variant == variant && e.Aperture == aperture))
                {
                    throw new InvalidDataException($"{name}: duplicate export for {variant}, f/{aperture}.");
                }

                exports.Add(new(file, variant, aperture, match.Groups[2].Success));
            }

            if (exports.Count == 0)
            {
                throw new InvalidDataException($"{name}: no exports found.");
            }

            scenes.Add(new(name, original, name is "wlosy" or "portret_4x",
                name is "slupek_1x" or "lezak_2x" or "liscie_1x" or "liscie_2x" or "liscie_4x",
                [.. exports.OrderBy(e => e.Variant, StringComparer.Ordinal).ThenBy(e => e.Aperture)]));
        }

        // Root manifests are not photographs. A misplaced photograph must not silently vanish from the audit.
        foreach (string path in Directory.GetFiles(directory))
        {
            if (Path.GetExtension(path).Equals(".heic", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Misplaced portrait photograph: {Path.GetFileName(path)}.");
            }

            ignored.Add(Path.GetFileName(path));
        }

        return new([.. scenes], [.. ignored], [.. notices]);
    }
}
