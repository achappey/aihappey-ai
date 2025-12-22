using Microsoft.EntityFrameworkCore;

namespace AIHappey.Telemetry.Models;

[Index(nameof(ModelName), IsUnique = true)]
public class Model
{
    public int Id { get; set; }

    public string ModelName { get; set; } = null!;

    public ICollection<Request> Requests { get; set; } = [];

    public Provider Provider { get; set; } = null!;

    public int ProviderId { get; set; }
}
