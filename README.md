# DefScribe

DefScribe generates Relax NG (`.rng`) schemas from RimWorld's managed assemblies for validating the structure of Def XML files.

## Usage

For the base game:

```sh
defscribe
```

For a mod, pass the path to its DLL:

```sh
defscribe /path/to/Mod.dll
```

If RimWorld is installed in a non-standard location, specify the managed assembly directory:

```sh
defscribe /path/to/Mod.dll --assembly-dir /path/to/RimWorld/Managed
```

## Validation

Use libxml2 to validate the generated schema:

```sh
xmllint --noout --relaxng Assembly-CSharp.rng Defs/Example.xml
```
