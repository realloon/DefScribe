using DefScribe;

try {
    var options = GeneratorOptions.Parse(args);
    var result = new SchemaGenerator(options).Generate();
    File.WriteAllText(options.OutputPath, result.Schema);

    Console.WriteLine($"Wrote {options.OutputPath}");
    Console.WriteLine($"Generated {result.ExactPatternCount} exact type patterns.");

    if (result.ConservativeTypes.Count > 0) {
        Console.Error.WriteLine($"Generated {result.ConservativeTypes.Count} conservative patterns:");
        foreach (var typeName in result.ConservativeTypes) {
            Console.Error.WriteLine($"  {typeName}");
        }
    }

    return 0;
} catch (ArgumentException error) {
    Console.Error.WriteLine(error.Message);
    Console.Error.WriteLine(GeneratorOptions.Usage);
    return 2;
} catch (Exception error) {
    Console.Error.WriteLine(error);
    return 1;
}