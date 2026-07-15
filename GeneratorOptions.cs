namespace DefScribe;

public sealed record GeneratorOptions(
    string AssemblyPath,
    string OutputPath,
    IReadOnlyList<string> AssemblyDirectories) {
    public const string Usage =
        "Usage: defscribe --assembly <Assembly-CSharp.dll> [--output <schema.rng>] [--assembly-dir <dir>]...";

    public static GeneratorOptions Parse(string[] args) {
        string? assemblyPath = null;
        var outputPath = Path.GetFullPath("rimworld.rng");
        var assemblyDirectories = new List<string>();

        for (var index = 0; index < args.Length; index++) {
            var option = args[index];
            if (option is not ("--assembly" or "--output" or "--assembly-dir") || index + 1 == args.Length) {
                throw new ArgumentException($"Invalid argument: {option}");
            }

            var value = args[++index];
            switch (option) {
                case "--assembly":
                    assemblyPath = Path.GetFullPath(value);
                    break;
                case "--output":
                    outputPath = Path.GetFullPath(value);
                    break;
                case "--assembly-dir":
                    assemblyDirectories.Add(Path.GetFullPath(value));
                    break;
            }
        }

        if (assemblyPath is null) {
            throw new ArgumentException("Missing --assembly.");
        }

        if (!File.Exists(assemblyPath)) {
            throw new ArgumentException($"Assembly does not exist: {assemblyPath}");
        }

        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (outputDirectory is not null) {
            Directory.CreateDirectory(outputDirectory);
        }

        return new GeneratorOptions(assemblyPath, outputPath, assemblyDirectories);
    }
}