# DefScribe

DefScribe generates XML Schema 1.0 (`.xsd`) files from RimWorld's managed assemblies for validating the structure of Def XML files.

## Usage

For the base game:

```sh
defscribe
```

For a mod, pass the path to its DLL:

```sh
defscribe /path/to/Mod.dll
```

The schema is written to the current working directory with the DLL's file name: `Assembly-CSharp.dll` produces `Assembly-CSharp.xsd`, and `Mod.dll` produces `Mod.xsd`.

If RimWorld is installed in a non-standard location, specify the managed assembly directory:

```sh
defscribe /path/to/Mod.dll --assembly-dir /path/to/RimWorld/Managed
```

## Validation

Use libxml2 to validate the generated schema:

```sh
xmllint --noout --schema Assembly-CSharp.xsd Defs/Example.xml
```

To associate an XML file with the schema in VS Code, add this processing instruction before its root element:

```xml
<?xml-model href="Assembly-CSharp.xsd" schematypens="http://www.w3.org/2001/XMLSchema"?>
```
