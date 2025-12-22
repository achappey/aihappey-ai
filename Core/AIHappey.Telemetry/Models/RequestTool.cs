using Microsoft.EntityFrameworkCore;

namespace AIHappey.Telemetry.Models;

[PrimaryKey(nameof(RequestId), nameof(ToolId))]

public class RequestTool
{
    public Tool Tool { get; set; } = null!;

    public int RequestId { get; set; }

    public int ToolId { get; set; }

    public Request Request { get; set; } = null!;
}
