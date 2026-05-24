# Coding Conventions

> How the project code should *read*. System behavior lives in `ARCHITECTURE.md`.
> Rules that `.editorconfig` and the .NET analyzers enforce mechanically are
> **not** repeated here. This file is for the judgment calls no analyzer can check.
>
> This file is expected to grow over time as rules accumulate across projects.

## Class layout

Member order, top to bottom:

1. Constants — `private const` first, `public const` last
2. Fields — `private readonly` first, then mutable `private`
3. Properties
4. Constructor
5. Methods — grouped by logical concern first, then within a group ordered by **call order** (if `MethodA` calls `MethodB`, `A` appears before `B`). No public-vs-private grouping. `ToString()` usually last
6. Static methods and operator overloads — at the very bottom
7. Do not use short form method `=>`; write method in full form with `{` and `}`

Notes:
- **The fields → properties → constructor order is deliberate and unusual.
  Do NOT "correct" it to the conventional constructor-first layout.** This is
  the house style.
- Nested types are allowed but discouraged — use only when the type is
  genuinely meaningless outside its parent.
- Interfaces are prefixed with `I` (`ISlotManager`).
- Do not use top-level statements. Entry points use an explicit `internal static class Program` with `private static async Task<int> Main(string[] args)`.
- Try to separate code by an empty line when there is a change in code logic, context or logical bound. **Exception:** do not add an empty line between statements so tightly coupled they form a single logical unit — e.g. declaring a variable and immediately checking or transforming it on the next line.

Full example showing the order:

```csharp
public sealed class SlotManager : ISlotManager
{
    private const int DefaultPortStep = 1000;
    // Empty line here to separate private consts vs public consts
    public const int TemplateSlotId = 0;

    private readonly IApolloConfigCloner _cloner;
    private readonly ILogger<SlotManager> _logger;
    // Empty line here to separate readonly vs mutable
    private int _allocatedCount;

    public IReadOnlyList<SlotInfo> Slots => _slots;

    public SlotManager(IApolloConfigCloner cloner, ILogger<SlotManager> logger)
    {
        _cloner = cloner;
        _logger = logger;
    }

    // Public entry point — appears before the private helpers it calls.
    public SlotInfo Allocate(int id)
    {
        ValidateId(id);
        // Empty line here to separate logical context of validation to processing
        /* ... */
        // Empty line here to separate processing code to return
        return CloneFromTemplate(id);
    }

    private void ValidateId(int id) { /* ... */ }

    private SlotInfo CloneFromTemplate(int id) { /* ... */ }

    public override string ToString()
    {
        return $"SlotManager({_allocatedCount} allocated)";
    }
}
```

## Naming

- Prefer boolean members to read as a question: prefix with `Is` or `Has` (`IsRunning`, `HasTemplate`).
- Prefer full names over abbreviations.
- Constants: prefer `public const`. Use `private const` only when the value has no meaning outside the class; in that case use PascalCase (never `_camelCase`). `.editorconfig` enforces this via a dedicated rule so the private-field underscore rule does not apply to `const` fields.

## Method shape

- Prefer early return / guard clauses over nested blocks.
- See **Control flow** for the nesting limit (stated once there).

## Async

- Return `Task` / `Task<T>`, never `async void`.
- Suffix async methods with `Async` (`StartSlotAsync`).
- Accept a `CancellationToken cancellationToken` and propagate it. Give it a
  default (`CancellationToken cancellationToken = default`) where appropriate.
- No `.Result` / `.Wait()` / `.GetAwaiter().GetResult()` — await all the way.

## Control flow

- Maximum 2 levels of nesting. Go deeper only with a clear, defensible reason.
- All control structures (`if`, `for`, `foreach`, `while`, etc.) must use braces. One-liners are a rare exception — only when a sequence of similar short guards appears together and the compact form genuinely reduces visual clutter. When in doubt, use braces.
  ```csharp
  // Default: always use braces
  if (!IsReady)
  {
      return;
  }

  // NOT OK: two lines without braces
  if (!IsReady)
      return;

  // Rare exception: one-liner only when many similar guards are grouped
  // and braces would add noise without adding clarity
  if (x < 0) throw new ArgumentOutOfRangeException(nameof(x));
  if (y < 0) throw new ArgumentOutOfRangeException(nameof(y));
  if (z < 0) throw new ArgumentOutOfRangeException(nameof(z));
  ```
- Use a ternary only when both branches are trivial and the short form reads
  better — e.g. picking a string from a bool:
  ```csharp
  string status = slot.IsRunning ? "running" : "stopped";
  ```

## Variable declarations

- Always use explicit type names. Use `var` sparingly — only for anonymous types or genuinely unreadable generic signatures (e.g. `Dictionary<string, List<SlotInfo>>`). Long but clear names like `WebApplicationBuilder` are still written explicitly.
- When the declared type is explicit on the left, use `new()` instead of repeating the full constructor name:
  ```csharp
  // Prefer:
  WindowsPrincipal principal = new(identity);

  // Over:
  WindowsPrincipal principal = new WindowsPrincipal(identity);
  ```

## Immutability and types

- Prefer `record` for objects meant to be immutable and that are short and simple.
- For records, prefer `init` over `set`. Use `required` only when no sensible default exists — if the SPEC or domain provides a value for a field, use it as the property default and omit `required`.

## Comments

- Comment to explain *complex or non-obvious* code — not to narrate the obvious.
- **Expression trees:** annotate them. For each `Expression` add a short comment
  showing what that fragment does, and at the point where lambdas/fragments are
  composed into the final tree, add a comment showing the **shape of the
  generated code**. Example:
  ```csharp
  var portSelector = Expression.Property(param, nameof(Slot.Port)); // p => p.Port
  var body = Expression.Equal(portSelector, Expression.Constant(targetPort)); // p => p.Port == targetPort
  var predicate = Expression.Lambda<Func<Slot, bool>>(body, param); // (Slot p) => p.Port == 48989
  ```

## File organization

Mechanical rules (file-scoped namespaces, `using` sorting, alias placement) are
enforced by `.editorconfig`. Nothing additional here.
