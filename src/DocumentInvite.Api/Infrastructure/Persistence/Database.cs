using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DocumentInvite.Api.Infrastructure.Persistence;

public static class Database
{
    /// <summary>True when the failure is a SQL Server unique index or constraint violation.</summary>
    public static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: 2601 or 2627 };
}
