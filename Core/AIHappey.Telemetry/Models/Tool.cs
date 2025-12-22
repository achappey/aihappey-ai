using Microsoft.EntityFrameworkCore;

namespace AIHappey.Telemetry.Models;

[Index(nameof(ToolName), IsUnique = true)]
public class Tool
{
    public int Id { get; set; }

    public string ToolName { get; set; } = null!;

    public ICollection<RequestTool> Requests { get; set; } = [];
}
