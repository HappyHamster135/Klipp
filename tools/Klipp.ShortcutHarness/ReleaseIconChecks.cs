using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

/// <summary>Uses Windows' native icon decoder for published and installer PE resources.</summary>
internal static class ReleaseIconChecks
{
    private static readonly int[] RequiredSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    internal static int Run(string[] args)
    {
        var options = Parse(args);
        var reference = Required(options, "--reference");
        var package = Required(options, "--package");
        if (!Directory.Exists(package)) throw new DirectoryNotFoundException(package);
        var referenceBytes = File.ReadAllBytes(reference);
        var referenceHash = SHA256.HashData(referenceBytes);
        CheckIcoFrames(reference, referenceBytes);
        var referencePixels = Program.NativeIcon.ReadPixels(reference);
        if (!referencePixels.Any(value => value != 0)) throw new InvalidOperationException("The reference icon renders no visible pixels.");
        var checks = 2;

        CheckIco(Path.Combine(package, "Assets", "Klipp.ico"));
        CheckExecutable(Path.Combine(package, "Klipp.Desktop.exe"));
        if (options.TryGetValue("--installer", out var installer)) CheckExecutable(installer);

        string? fixtures = null;
        if (options.TryGetValue("--full-package", out var fullPackage))
        {
            fixtures = Path.Combine(Path.GetTempPath(), "Klipp-Release-Icon-QA-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fixtures);
            using var archive = ZipFile.OpenRead(fullPackage);
            var app = Extract("lib/app/Klipp.Desktop.exe", "Klipp.Desktop.exe");
            var stub = Extract("lib/app/Klipp_ExecutionStub.exe", "Klipp_ExecutionStub.exe");
            var ico = Extract("lib/app/Assets/Klipp.ico", "Klipp.ico");
            CheckExecutable(app);
            CheckExecutable(stub);
            CheckIco(ico);

            string Extract(string entryPath, string filename)
            {
                var entries = archive.Entries.Where(entry => string.Equals(entry.FullName.Replace('\\', '/'), entryPath,
                    StringComparison.Ordinal)).ToArray();
                if (entries.Length != 1) throw new InvalidOperationException($"Expected exactly one {entryPath} in {fullPackage}.");
                var destination = Path.Combine(fixtures, filename);
                using var input = entries[0].Open();
                using var output = File.Create(destination);
                input.CopyTo(output);
                return destination;
            }
        }

        Console.WriteLine($"PASS: {checks} native release icon checks." + (fixtures is null ? "" : $" Fixtures: {fixtures}"));
        return 0;

        void CheckIco(string path)
        {
            var bytes = File.ReadAllBytes(path);
            if (!SHA256.HashData(bytes).SequenceEqual(referenceHash))
                throw new InvalidOperationException($"The distributed ICO differs from the source artwork: {path}");
            CheckIcoFrames(path, bytes);
            if (!Program.NativeIcon.ReadPixels(path).SequenceEqual(referencePixels))
                throw new InvalidOperationException($"The distributed ICO renders different pixels: {path}");
            checks += 3;
        }

        void CheckExecutable(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("Release executable is missing.", path);
            if (!Program.NativeIcon.ReadExecutablePixels(path).SequenceEqual(referencePixels))
                throw new InvalidOperationException($"Windows extracted the wrong application icon from {path}.");
            checks++;
        }
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var permitted = new HashSet<string>(["--reference", "--package", "--installer", "--full-package"], StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == "--verify-release-icons") continue;
            var name = args[index];
            if (!permitted.Contains(name) || index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("Invalid or missing release icon option: " + name);
            if (!options.TryAdd(name, Path.GetFullPath(args[++index]))) throw new ArgumentException("Duplicate option: " + name);
        }
        return options;
    }

    private static string Required(IReadOnlyDictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) ? value : throw new ArgumentException("Missing " + name);

    private static void CheckIcoFrames(string path, byte[] bytes)
    {
        if (bytes.Length < 6 || BinaryPrimitives.ReadUInt16LittleEndian(bytes) != 0 ||
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2)) != 1)
            throw new InvalidOperationException("Invalid Windows icon header: " + path);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4));
        if (count != RequiredSizes.Length || bytes.Length < 6 + count * 16)
            throw new InvalidOperationException("The ICO must contain every required Windows icon size: " + path);
        var actualSizes = new List<int>();
        for (var index = 0; index < count; index++)
        {
            var entry = bytes.AsSpan(6 + index * 16, 16);
            var width = entry[0] == 0 ? 256 : entry[0];
            var height = entry[1] == 0 ? 256 : entry[1];
            var planes = BinaryPrimitives.ReadUInt16LittleEndian(entry[4..]);
            var depth = BinaryPrimitives.ReadUInt16LittleEndian(entry[6..]);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
            if (width != height || planes != 1 || depth != 32 || size == 0 ||
                offset < 6 + count * 16 || (ulong)offset + size > (ulong)bytes.Length)
                throw new InvalidOperationException("Invalid 32-bit ICO frame: " + path);
            actualSizes.Add(width);
        }
        if (!actualSizes.Order().SequenceEqual(RequiredSizes))
            throw new InvalidOperationException("Unexpected or duplicate ICO sizes: " + path);
    }
}
