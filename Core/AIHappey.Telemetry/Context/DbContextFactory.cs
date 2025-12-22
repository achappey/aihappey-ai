using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using AIHappey.Telemetry.Models;

namespace AIHappey.Telemetry.Context;

public class DbContextFactory : IDesignTimeDbContextFactory<AIHappeyTelemetryDatabaseContext>
{
  public AIHappeyTelemetryDatabaseContext CreateDbContext(string[] args)
  {
    if (args.Length != 1)
    {
      throw new InvalidOperationException("Please provide connection string like this: dotnet ef database update -- \"yourConnectionString\"");
    }

    var optionsBuilder = new DbContextOptionsBuilder<AIHappeyTelemetryDatabaseContext>();
    optionsBuilder.UseSqlServer(args[0], options => options.EnableRetryOnFailure());

    return new AIHappeyTelemetryDatabaseContext(optionsBuilder.Options);
  }
}
