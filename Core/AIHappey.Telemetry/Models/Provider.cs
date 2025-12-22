using Microsoft.EntityFrameworkCore;

namespace AIHappey.Telemetry.Models;

[Index(nameof(Name), IsUnique = true)]
public class Provider
{
    public int Id { get; set; }

    public string Name { get; set; } = null!;

    public ICollection<Model> Models { get; set; } = [];
}
