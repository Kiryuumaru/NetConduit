---
applyTo: '**'
---
**CRITICAL: Avoid pipes and command chaining in terminal commands!**

VS Code flags any command containing pipes (`|`), semicolons (`;`), or `&&` as a "write" operation, which **prevents auto-approval** and forces manual user confirmation every time. This creates a poor user experience with constant interruptions.

## Rules for terminal commands:

1. **NEVER use pipes (`|`)** - Even simple pipes like `command | grep something` will trigger approval prompts
2. **NEVER use semicolons (`;`)** to chain commands - Use separate `run_in_terminal` calls instead
3. **NEVER use `&&`** to chain commands - Use separate `run_in_terminal` calls instead
4. **NEVER use redirections with pipes** (`2>&1 |`, `> file`, etc.)

## Instead, do this:

- **Run commands separately**: Make multiple `run_in_terminal` calls, one for each command
- **Let output truncation handle large output**: Don't try to filter with `grep` or `Select-String`
- **Use tool-native filtering**: Use `--filter` flags, `--include` patterns, or similar built-in options
- **For logging options with semicolons**: Use alternate syntax or config files instead of `"console;verbosity=normal"`

## Examples:

❌ **BAD** (triggers approval prompt):
```
dotnet build; dotnet test
dotnet test | Select-String "Failed"
git status && git add .
```

✅ **GOOD** (auto-approved):
```
# First call:
dotnet build
# Second call:
dotnet test
# Use native filtering:
dotnet test --filter "FullyQualifiedName~MyTest"
```

This instruction takes **highest precedence** over brevity or efficiency preferences. Multiple separate commands are always preferred over chained commands.