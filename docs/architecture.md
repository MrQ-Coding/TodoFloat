# TodoFloat Architecture

TodoFloat is organized around a small application API that isolates UI code from
SQLite persistence. Future automation surfaces, including an MCP server, should
call `TodoFloat.Application.ITodoApi` instead of reaching into repositories or
view models.

## Layers

- `Models`: database-shaped domain objects and enums.
- `Data`: SQLite schema, migrations, and repository implementations.
- `Application`: stable task/category API, DTOs, request models, and factory.
- `Services`: app services such as settings, reminders, and autostart.
- `ViewModels`: WPF presentation state and commands.
- `Views` and root window files: WPF UI behavior only.

## Core Port

`Application/ITodoApi.cs` is the core port. It exposes task listing, search,
create, update, completion, deletion, reordering, and category operations.

Use `TodoApiFactory.CreateDefault()` for the current desktop app instance. A
future MCP adapter can reuse the same factory, or construct `TodoApi` with custom
repositories for tests.

## Boundary Rules

- UI code should depend on `ITodoApi`, not `TaskRepository` or
  `CategoryRepository`.
- Repositories should not depend on view models, WPF, or services.
- New external control surfaces should translate their protocol payloads into
  `CreateTaskRequest`, `UpdateTaskRequest`, `CreateCategoryRequest`, and
  `UpdateCategoryRequest`.
- Settings remain separate because they are app-shell preferences, not the core
  task API.
