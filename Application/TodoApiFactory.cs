using TodoFloat.Data;

namespace TodoFloat.Application;

public static class TodoApiFactory
{
    public static ITodoApi CreateDefault() =>
        new TodoApi(new TaskRepository(), new CategoryRepository());
}
