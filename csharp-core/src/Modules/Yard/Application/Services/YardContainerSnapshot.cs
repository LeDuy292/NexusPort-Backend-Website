using Microsoft.EntityFrameworkCore;
using NexusPort.Infrastructure.Database;

namespace NexusPort.Modules.Yard.Application.Services;

// Receiving and relocation must never read cargo/seals from the shared identity.
public class YardContainerSnapshot
{
    public Guid ContainerId { get; set; }
    public Guid? VisitId { get; set; }
    public string TypeCode { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? SealNumber { get; set; }
    public string? CargoType { get; set; }
    public decimal? GrossWeightKg { get; set; }

    public static Task<YardContainerSnapshot?> ReadAsync(AppDbContext context, string number, CancellationToken cancellationToken) =>
        context.Database.SqlQueryRaw<YardContainerSnapshot>(@"SELECT c.id AS ""ContainerId"",v.id AS ""VisitId"",ct.code AS ""TypeCode"",
          c.status::text AS ""Status"",v.seal_number AS ""SealNumber"",v.cargo_type::text AS ""CargoType"",v.gross_weight_kg AS ""GrossWeightKg""
          FROM containers c JOIN container_types ct ON ct.id=c.container_type_id
          LEFT JOIN container_visits v ON v.container_id=c.id AND v.visit_status='active' WHERE c.container_no={0}",number).FirstOrDefaultAsync(cancellationToken);
}
