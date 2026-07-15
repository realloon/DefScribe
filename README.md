# DefScribe

DefScribe 从 RimWorld 的托管程序集生成 Relax NG (`.rng`) schema，用于检查 Def XML 的结构。

它以 Mono.Cecil 读取程序集，不执行游戏或 Mod 代码。能从元数据可靠得出的结构会被约束；自定义 XML 加载器、多态基类和无法解析的类型会降为宽松的 `any-content`，并在标准错误输出中列出。

## 使用

```sh
dotnet run -- --assembly /path/to/Assembly-CSharp.dll --output rimworld.rng
```

处理独立 Mod 程序集时，额外提供 RimWorld 的 `Managed` 目录以解析基类：

```sh
dotnet run -- \
  --assembly /path/to/Mod.dll \
  --assembly-dir /path/to/RimWorld/.../Data/Managed \
  --output mod.rng
```

生成的 schema 校验 XML 结构、字段名、普通集合、字典、枚举和 Def 引用的 `defName` 形式。它不校验引用的 defName 是否确实存在，也不尝试重现每个自定义加载器的专用语法。

可用 libxml2 验证生成结果：

```sh
xmllint --noout --relaxng rimworld.rng Defs/Example.xml
```
