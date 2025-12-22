using Microsoft.EntityFrameworkCore;

namespace AIHappey.Telemetry.Models;

[Index(nameof(UserId), IsUnique = true)]
public class User
{
    public int Id { get; set; }

    public string UserId { get; set; } = null!;

    public string Username { get; set; } = null!;

      public ICollection<Request> Requests { get; set; } = new List<Request>();
}
