# CubeMarching – Claude Instructions

## C# Language Version
Unity's compiler targets **C# 9**. Do not use C# 10+ syntax even if the IDE linter suggests it. Specifically avoid:

- Collection expressions: `[new Foo(), ...]` → use `new Foo[] { new Foo(), ... }`
- Primary constructors: `struct Foo(int x)` → use an explicit constructor body
- Target-typed `new` in collection initializers (also C# 12 in that form)
