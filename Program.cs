using DefScribe;

return await ProgramMain.RunAsync(args);

internal static class ProgramMain {
    public static Task<int> RunAsync(string[] args) {
        try {
            var options = GeneratorOptions.Parse(args);
            var result = new SchemaGenerator(options).Generate();
            File.WriteAllText(options.OutputPath, result.Schema);

            Console.WriteLine($"Wrote {options.OutputPath}");
            Console.WriteLine($"Generated {result.ExactPatternCount} exact type patterns.");

            if (result.ConservativeTypes.Count <= 0) {
                return Task.FromResult(0);
            }

            Console.Error.WriteLine($"Generated {result.ConservativeTypes.Count} conservative patterns:");
            foreach (var typeName in result.ConservativeTypes) {
                Console.Error.WriteLine($"  {typeName}");
            }

            return Task.FromResult(0);
        } catch (ArgumentException error) {
            Console.Error.WriteLine(error.Message);
            Console.Error.WriteLine(GeneratorOptions.Usage);
            return Task.FromResult(2);
        } catch (Exception error) {
            Console.Error.WriteLine(error);
            return Task.FromResult(1);
        }
    }
}