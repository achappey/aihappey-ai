using Microsoft.EntityFrameworkCore;

namespace AIHappey.Telemetry.Models;

public class Request
{
    public int Id { get; set; }

    public string? RequestId { get; set; }

    //public string Model { get; set; } = null!;

    public string? ToolChoice { get; set; }

    public float Temperature { get; set; }

    public int InputTokens { get; set; }

    public int TotalTokens { get; set; }

    public User User { get; set; } = null!;

    public int UserId { get; set; }

    public ICollection<RequestTool> Tools { get; set; } = [];

    public Model Model { get; set; } = null!;

    public int ModelId { get; set; }

    [Precision(3)]
    public DateTime StartedAt { get; set; }

    [Precision(3)]
    public DateTime EndedAt { get; set; }

    public RequestType RequestType { get; set; }
}

public enum RequestType
{
    Chat,
    Sampling,
    Completion
}