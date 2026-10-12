using Em.Api.Shared;
using Microsoft.EntityFrameworkCore;

namespace Em.Api.Core.Log;

/// <summary>Context over <c>ta_LogAudit</c> of any application database (outbox), created per connection.</summary>
internal sealed class LogOutboxDbContext(DbContextOptions<LogOutboxDbContext> options) : EmDbContext(options)
{
   public DbSet<ta_LogAudit> Audit => Set<ta_LogAudit>();

   public static LogOutboxDbContext Create(DbConnectionInfo connection) {
      var options = new DbContextOptionsBuilder<LogOutboxDbContext>();
      options.UseEmProvider(connection.Provider, connection.ConnectionString);
      return new LogOutboxDbContext(options.Options);
   }
}
