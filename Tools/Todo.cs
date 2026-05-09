namespace Imp.Tools;

// Session-scoped task checklist the model uses to plan and track multi-step
// work during a single contract run. Ported from nb/TodoManager.cs and
// nb/TodoTool.cs. Simplified: one class per concern (no separate Manager +
// Tool split — imp registers the tool functions inline in Tools.Create).
//
// The `Content` field is the unique key — todo_write updates existing items
// in place, adds new ones, and removes any marked `cancelled`. Cleared when
// the contract run ends (TodoManager lives on ExecutorState).

public enum TodoStatus
{
    Pending,
    InProgress,
    Completed,
    Cancelled,
}

public class Todo
{
    public required string Content { get; init; }
    public TodoStatus Status { get; set; }
}

public record TodoChange(string Content, string Status);

public class TodoManager
{
    readonly List<Todo> _todos = [];

    public List<string> ApplyChanges(IList<TodoChange> changes)
    {
        var applied = new List<string>();
        foreach (var c in changes)
        {
            if (string.IsNullOrWhiteSpace(c.Content))
            {
                applied.Add("[ERROR] Empty content, skipped");
                continue;
            }
            if (!TryParseStatus(c.Status, out var status))
            {
                applied.Add($"[ERROR] Invalid status '{c.Status}' for '{c.Content}' — use pending | in_progress | completed | cancelled");
                continue;
            }

            var existing = _todos.FirstOrDefault(t => t.Content == c.Content);

            if (status == TodoStatus.Cancelled)
            {
                if (existing is not null)
                {
                    _todos.Remove(existing);
                    applied.Add($"[CANCELLED] {c.Content}");
                }
                else
                {
                    applied.Add($"[SKIP] not found: {c.Content}");
                }
            }
            else if (existing is null)
            {
                _todos.Add(new Todo { Content = c.Content, Status = status });
                applied.Add($"[ADDED {StatusLabel(status)}] {c.Content}");
            }
            else
            {
                existing.Status = status;
                applied.Add($"[UPDATED {StatusLabel(status)}] {c.Content}");
            }
        }
        return applied;
    }

    public string Render()
    {
        if (_todos.Count == 0) return "(no todos)";
        return string.Join("\n", _todos.Select(t => $"- [{StatusLabel(t.Status)}] {t.Content}"));
    }

    static bool TryParseStatus(string? s, out TodoStatus status)
    {
        switch (s?.Trim().ToLowerInvariant())
        {
            case "pending":
                status = TodoStatus.Pending; return true;
            case "in_progress":
            case "inprogress":
            case "in-progress":
                status = TodoStatus.InProgress; return true;
            case "completed":
            case "done":
                status = TodoStatus.Completed; return true;
            case "cancelled":
            case "canceled":
                status = TodoStatus.Cancelled; return true;
            default:
                status = default; return false;
        }
    }

    public static string StatusLabel(TodoStatus s) => s switch
    {
        TodoStatus.Pending => "pending",
        TodoStatus.InProgress => "in_progress",
        TodoStatus.Completed => "completed",
        TodoStatus.Cancelled => "cancelled",
        _ => s.ToString().ToLowerInvariant(),
    };
}
