[![](https://img.shields.io/github/actions/workflow/status/soenneker/Soenneker.Git.Runners.Windows/build-and-test.yml?style=for-the-badge)](https://github.com/soenneker/Soenneker.Git.Runners.Windows/actions/workflows/build-and-test.yml)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/Soenneker.Git.Runners.Windows/daily-automatic-update.yml?style=for-the-badge&label=Daily%20Update)](https://github.com/soenneker/Soenneker.Git.Runners.Windows/actions/workflows/daily-automatic-update.yml)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/Soenneker.Git.Runners.Windows/codeql.yml?label=CodeQL&style=for-the-badge)](https://github.com/soenneker/Soenneker.Git.Runners.Windows/actions/workflows/codeql.yml)

# Soenneker.Git.Runners.Windows

Defines the file operations util contract.

> This is an automation runner, not a package intended for application consumption.

## What the runner does

- `IFileOperationsUtil.Process(cancellationToken)` — Processes the pending work managed by the file operations.
- `Constants.Library` — The library.
- `ConsoleHostedService.StartAsync(cancellationToken)` — Starts the console hosted service and begins its background work.
- `ConsoleHostedService.StopAsync(cancellationToken)` — Stops the console hosted service and waits for its background work to finish.

## What you get

- `IFileOperationsUtil` — Defines the file operations util contract.
- `Constants` — Represents the constants.
- `ConsoleHostedService` — Represents the console hosted service.

## API at a glance

| API | What it does | Result / important behavior |
| --- | --- | --- |
| `IFileOperationsUtil.Process(cancellationToken)` | Processes the pending work managed by the file operations. | A task whose result is the text returned by process. |
| `ConsoleHostedService.StartAsync(cancellationToken)` | Starts the console hosted service and begins its background work. | A task that completes after the console hosted service has started. |
| `ConsoleHostedService.StopAsync(cancellationToken)` | Stops the console hosted service and waits for its background work to finish. | A task that completes after the console hosted service has stopped. |

## Practical notes

- Cancellation stops pending work; it does not undo work that has already completed.
