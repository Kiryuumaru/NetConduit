---
applyTo: '**'
---
# Code Quality Rules

## Fix Hygiene

**Failed fix = full revert.**

- NEVER leave non-working code from failed fix attempts
- NEVER apply fix B on top of failed fix A
- NEVER comment out failed code instead of removing

---

## Zero Warnings

Build MUST complete with 0 warnings and 0 errors. Every warning is a potential bug.

---

## Nullable Handling

The null-forgiving operator (`!`) is prohibited except when ALL conditions are met:
1. Value is proven non-null at that point
2. Compiler cannot infer this due to analysis limitations
3. Comment explains why it is safe
4. No reasonable restructuring alternative exists

Nullable handling patterns:
- For possibly null value, USE `?? throw new InvalidOperationException()`
- For null check, USE `if (x is null) return;`
- For optional value, DECLARE as nullable type `T?`
- For constructor parameter, USE `?? throw new ArgumentNullException(nameof(param))`

---

## Reliability Principles

- MUST make illegal states unrepresentable using the type system
- MUST validate at boundaries by checking all inputs at system edges
- MUST fail fast by throwing early rather than propagating bad state
- MUST be explicit and never rely on implicit behavior or defaults

---

## Code Longevity

- MUST use stable, documented APIs
- MUST avoid implementation-specific tricks
- MUST document reasoning, not mechanics

---

## Primary Constructors

When a constructor only stores parameters without additional logic, USE primary constructors (C# 12).

Traditional constructor (avoid when only storing):
```csharp
internal sealed class DomainEventDispatcher : IDomainEventDispatcher
{
    private readonly IEnumerable<IDomainEventHandler> _handlers;

    public DomainEventDispatcher(IEnumerable<IDomainEventHandler> handlers)
    {
        _handlers = handlers;
    }

    public void DoWork() => _handlers.ToList();
}
```

Primary constructor (preferred):
```csharp
internal sealed class DomainEventDispatcher(IEnumerable<IDomainEventHandler> handlers) : IDomainEventDispatcher
{
    public void DoWork() => handlers.ToList();
}
```

Primary constructor rules:
- USE when constructor only assigns parameters to fields
- USE parameter name directly in methods (no underscore prefix)
- KEEP traditional constructor when validation or transformation logic is needed
- KEEP traditional constructor when multiple constructors are required

---

## Commenting

### Core Principle

Comments exist for future readers with no conversation context. The codebase stands alone.

### Prohibited Comments

- NEVER use `// TODO:` comments
- NEVER use `// HACK:` comments
- NEVER use `// FIXME:` comments
- NEVER use `#pragma warning disable`
- NEVER use `[SuppressMessage]` attributes
- NEVER use `var x = value!;` without justification comment
- NEVER write comments referencing prompts: "As requested", "Per instruction", "Added because asked"
- NEVER write comments referencing conversation: "As discussed", "Per our conversation", "Following the plan"
- NEVER write meta-comments: "NEW:", "CHANGED:", "This is the fix"
- NEVER write obvious descriptions: "Loop through list", "Return result", "Check if null"
- NEVER write comments that would not make sense 2 years later without conversation context
- NEVER write XML documentation on internal implementation classes
- NEVER describe what code does when code is self-explanatory
- NEVER ignore compiler warnings
- NEVER suppress warnings instead of fixing root cause

### Required Comments

- MUST document non-obvious behavior: security implications, order dependencies
- MUST document external requirements: RFC references, spec compliance
- MUST document edge cases: intentional empty returns, fail-safe defaults
- MUST document reasoning: why this approach, not what it does

### Comment Decision

1. Is this obvious from the code? -> No comment
2. Does this reference conversation? -> No comment
3. Would future reader need this context? -> Comment
4. Is there non-obvious reasoning? -> Comment

---

## XML Documentation

XML docs (`///`) belong on interfaces only. Implementations do not require XML documentation.

- Public interface MUST have XML docs
- Interface methods MUST have XML docs
- Implementation class MUST NOT have XML docs
- Implementation methods MUST NOT have XML docs
