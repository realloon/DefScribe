namespace DefScribe;

public sealed record GeneratorOptions(string AssemblyPath, string OutputPath, string AssemblyDirectory) {
    public const string Usage =
        "Usage: defscribe [<assembly.dll>] [--assembly-dir <RimWorld Managed>]";

    private static string DefaultAssemblyDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Steam",
        "steamapps",
        "common",
        "RimWorld",
        "RimWorldMac.app",
        "Contents",
        "Resources",
        "Data",
        "Managed");

    public static GeneratorOptions Parse(string[] args) {
        string? assemblyArgument = null;
        string? assemblyDirectoryArgument = null;

        for (var index = 0; index < args.Length; index++) {
            var argument = args[index];
            if (argument.Length == 0 || argument[0] != '-') {
                if (assemblyArgument is not null) {
                    throw new ArgumentException("Only one assembly path can be specified.");
                }

                assemblyArgument = argument;
                continue;
            }

            if (argument != "--assembly-dir" || index + 1 == args.Length) {
                throw new ArgumentException($"Invalid argument: {argument}");
            }

            var value = args[++index];
            switch (argument) {
                case "--assembly-dir":
                    if (assemblyDirectoryArgument is not null) {
                        throw new ArgumentException("--assembly-dir can only be specified once.");
                    }

                    assemblyDirectoryArgument = value;
                    break;
            }
        }

        var assemblyDirectory = Path.GetFullPath(assemblyDirectoryArgument ?? DefaultAssemblyDirectory);
        if (!Directory.Exists(assemblyDirectory)) {
            throw new ArgumentException(
                $"RimWorld Managed directory does not exist: {assemblyDirectory}\n" +
                "Pass --assembly-dir to specify it explicitly.");
        }

        var assemblyPath = Path.GetFullPath(assemblyArgument ?? Path.Combine(assemblyDirectory, "Assembly-CSharp.dll"));
        if (!File.Exists(assemblyPath)) {
            throw new ArgumentException($"Assembly does not exist: {assemblyPath}");
        }

        var outputPath = Path.Combine(
            Environment.CurrentDirectory,
            Path.ChangeExtension(Path.GetFileName(assemblyPath), ".rng"));

        return new GeneratorOptions(assemblyPath, outputPath, assemblyDirectory);
    }
}