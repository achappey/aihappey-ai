using Microsoft.EntityFrameworkCore;
using AIHappey.Telemetry.Models;

namespace AIHappey.Telemetry.Context;

public class AIHappeyTelemetryDatabaseContext(DbContextOptions<AIHappeyTelemetryDatabaseContext> options) : DbContext(options)
{
  public DbSet<Request> Requests { get; set; } = null!;

  public DbSet<Tool> Tools { get; set; } = null!;

  public DbSet<RequestTool> RequestTools { get; set; } = null!;

  public DbSet<User> Users { get; set; } = null!;

  public DbSet<Model> Models { get; set; } = null!;

  public DbSet<Provider> Providers { get; set; } = null!;

}
