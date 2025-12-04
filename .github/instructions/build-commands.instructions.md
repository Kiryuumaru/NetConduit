---
applyTo: '**'
---
# Build Commands Reference

When building, running, or initializing this project, use the following commands:

## NUKE Build Commands

| Command | Description |
|---------|-------------|
| `.\build.ps1 clean` | Clean all build artifacts (bin, obj, .vs folders) |
| `.\build.ps1 githubworkflow` | Generate GitHub Actions workflow files |

## .NET Commands

| Command | Description |
|---------|-------------|
| `dotnet build` | Build the solution |
| `dotnet test` | Run all tests |

## First-Time Setup

When setting up a fresh clone:

1. Run `dotnet build` to build the solution
