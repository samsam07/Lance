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
- Try to separate code by an empty line when there is a change in code logic, context or logical bound.

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
- Use a ternary only when both branches are trivial and the short form reads
  better — e.g. picking a string from a bool:
  ```csharp
  var status = slot.IsRunning ? "running" : "stopped";
  ```

## Immutability and types

- Prefer `record` for objects meant to be immutable and that are short and
  simple.
- For records, prefer `init` over `set`, and use `required` where appropriate.

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
