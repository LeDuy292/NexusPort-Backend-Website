using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using NexusPort.Infrastructure.Authentication;
using NexusPort.Infrastructure.Database;
using NexusPort.Modules.ServiceCatalog.Domain;

namespace NexusPort.Modules.ServiceCatalog.Application;

public sealed class CatalogService(AppDbContext db, ICurrentUser user)
{
    private const string ServiceFields = "id,code,name,description,direction,unit,icon,conditions,is_active AS \"isActive\",requires_approval AS \"requiresApproval\",version,updated_at AS \"updatedAt\"";
    private const string PriceFields = "id,service_id AS \"serviceId\",size,equipment,amount,currency,unit,effective_from::text AS \"effectiveFrom\",effective_to::text AS \"effectiveTo\",is_active AS \"isActive\",note,created_at AS \"createdAt\"";
    private static object[] Args(params object?[] args) => args.Select((a, i) => (object)(a == null ? new NpgsqlParameter("p" + i, NpgsqlDbType.Unknown) { Value = DBNull.Value } : new NpgsqlParameter("p" + i, a))).ToArray();
    private async Task<List<JsonObject>> Rows(string sql, CancellationToken ct, params object?[] args)
        => (await db.Database.SqlQueryRaw<string>("SELECT row_to_json(q)::text AS \"Value\" FROM (" + sql + ") q", Args(args)).ToListAsync(ct)).Select(s => JsonNode.Parse(s)!.AsObject()).ToList();
    private Task<int> Exec(string sql, CancellationToken ct, params object?[] args) => db.Database.ExecuteSqlRawAsync(sql, Args(args), ct);
    private Guid Actor => user.UserId ?? throw new CatalogException("Vui lòng đăng nhập.", 401, "UNAUTHORIZED");
    private async Task Authorize(bool admin, CancellationToken ct)
    {
        var accounts = await Rows("SELECT role FROM users WHERE id=@p0 AND is_active", ct, Actor);
        var role = accounts.FirstOrDefault()?["role"]?.GetValue<string>();
        CatalogRules.Require(role != null && string.Equals(role, user.Role, StringComparison.OrdinalIgnoreCase), "Tài khoản không còn quyền sử dụng danh mục dịch vụ.", 403, "FORBIDDEN");
        CatalogRules.Require(admin ? role is "Administrator" or "Admin" : role is "Administrator" or "Admin" or "Transport Company" or "Carrier" or "Carrier Staff", "Bạn không có quyền thực hiện thao tác này.", 403, "FORBIDDEN");
    }
    private async Task<JsonObject> Find(Guid id, CancellationToken ct, bool locked = false)
    {
        var rows = await Rows($"SELECT {ServiceFields} FROM eport_services WHERE id=@p0" + (locked ? " FOR UPDATE" : ""), ct, id);
        return rows.SingleOrDefault() ?? throw new CatalogException("Không tìm thấy dịch vụ.", 404, "CATALOG_NOT_FOUND");
    }
    private Task<int> Audit(Guid id, string action, JsonObject? before, JsonObject after, CancellationToken ct) => Exec("INSERT INTO eport_catalog_audit(id,entity_id,actor_id,action,before_data,after_data) VALUES(@p0,@p1,@p2,@p3,CAST(@p4 AS jsonb),CAST(@p5 AS jsonb))", ct, Guid.NewGuid(), id, Actor, action, before?.ToJsonString(), after.ToJsonString());
    public async Task<object> List(CancellationToken ct)
    {
        await Authorize(false, ct);
        var admin = user.Role is "Administrator" or "Admin";
        var services = await Rows($"SELECT {ServiceFields} FROM eport_services ORDER BY CASE code WHEN 'EXPORT_DROPOFF' THEN 1 WHEN 'IMPORT_PICKUP' THEN 2 WHEN 'REEFER_POWER' THEN 3 WHEN 'YARD_RELOCATION' THEN 4 WHEN 'STORAGE_EXTENSION' THEN 5 WHEN 'CONTAINER_WEIGH' THEN 6 ELSE 7 END,created_at,code", ct);
        var prices = admin ? await Rows($"SELECT {PriceFields} FROM eport_tariffs ORDER BY effective_from DESC,created_at DESC", ct) : [];
        foreach (var service in services) { service["tariffs"] = new JsonArray(prices.Where(r => r["serviceId"]!.ToString() == service["id"]!.ToString()).Select(r => (JsonNode)r.DeepClone()).ToArray()); service["pricingManagedByDispatcher"] = !admin; }
        return new { asOf = CatalogRules.Today, services, metadata = new { directions = CatalogRules.Directions, units = CatalogRules.Units, sizes = CatalogRules.Sizes, equipment = CatalogRules.Equipment, icons = CatalogRules.Icons } };
    }
    public async Task<JsonObject> Save(Guid? id, ServiceInput request, CancellationToken ct)
    {
        await Authorize(true, ct);
        var input = CatalogRules.Validate(request);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var before = id.HasValue ? await Find(id.Value, ct, true) : null;
        if (before != null)
        {
            CatalogRules.Require(request.ExpectedVersion.HasValue && before["version"]!.GetValue<int>() == request.ExpectedVersion, "Dịch vụ vừa được cập nhật. Tải lại trước khi lưu.", 409, "CATALOG_VERSION_CONFLICT");
            CatalogRules.Require(before["code"]!.ToString() == input.Code, "Mã dịch vụ được giữ cố định để bảo toàn liên kết.");
            if (before["unit"]!.ToString() != input.Unit) CatalogRules.Require((await Rows("SELECT id FROM eport_tariffs WHERE service_id=@p0 LIMIT 1", ct, id)).Count == 0, "Dịch vụ đã có biểu phí. Giữ đơn vị tính để bảo toàn lịch sử giá.");
        }
        var serviceId = id ?? Guid.NewGuid();
        var args = new object?[] { serviceId, input.Code, input.Name, input.Description, input.Direction, input.Unit, input.Icon, JsonSerializer.Serialize(input.Conditions), input.IsActive, input.RequiresApproval };
        if (id.HasValue) await Exec("UPDATE eport_services SET name=@p2,description=@p3,direction=@p4,unit=@p5,icon=@p6,conditions=CAST(@p7 AS jsonb),is_active=@p8,requires_approval=@p9,version=version+1,updated_at=now() WHERE id=@p0", ct, args);
        else await Exec("INSERT INTO eport_services(id,code,name,description,direction,unit,icon,conditions,is_active,requires_approval) VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,CAST(@p7 AS jsonb),@p8,@p9)", ct, args);
        var after = await Find(serviceId, ct);
        await Audit(serviceId, before == null ? "SERVICE_CREATED" : "SERVICE_UPDATED", before, after, ct);
        await tx.CommitAsync(ct); return after;
    }
    public async Task<JsonObject> AddTariff(Guid id, TariffInput request, CancellationToken ct)
    {
        await Authorize(true, ct);
        var input = CatalogRules.Validate(request);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var service = await Find(id, ct, true);
        var overlap = await Rows("SELECT id FROM eport_tariffs WHERE service_id=@p0 AND size=@p1 AND equipment=@p2 AND is_active AND effective_from<=COALESCE(CAST(@p4 AS date),'infinity'::date) AND COALESCE(effective_to,'infinity'::date)>=CAST(@p3 AS date)", ct, id, input.Size, input.Equipment, input.EffectiveFrom, input.EffectiveTo);
        CatalogRules.Require(overlap.Count == 0, "Khoảng hiệu lực trùng biểu phí cùng kích thước và nhóm container. Ngừng áp dụng biểu phí cũ hoặc chọn khoảng ngày khác.", 409, "TARIFF_OVERLAP");
        var tariffId = Guid.NewGuid();
        await Exec("INSERT INTO eport_tariffs(id,service_id,size,equipment,amount,unit,effective_from,effective_to,note,created_by) VALUES(@p0,@p1,@p2,@p3,@p4,@p5,CAST(@p6 AS date),CAST(@p7 AS date),@p8,@p9)", ct, tariffId, id, input.Size, input.Equipment, input.Amount, service["unit"]!.ToString(), input.EffectiveFrom, input.EffectiveTo, input.Note, Actor);
        var after = (await Rows($"SELECT {PriceFields} FROM eport_tariffs WHERE id=@p0", ct, tariffId)).Single();
        await Audit(id, "TARIFF_CREATED", null, after, ct); await tx.CommitAsync(ct); return after;
    }
    public async Task<JsonObject> Retire(Guid id, Guid tariffId, CancellationToken ct)
    {
        await Authorize(true, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await Find(id, ct, true);
        var before = (await Rows($"SELECT {PriceFields} FROM eport_tariffs WHERE id=@p0 AND service_id=@p1 FOR UPDATE", ct, tariffId, id)).SingleOrDefault() ?? throw new CatalogException("Không tìm thấy biểu phí.", 404);
        if (!before["isActive"]!.GetValue<bool>()) { await tx.CommitAsync(ct); return before; }
        await Exec("UPDATE eport_tariffs SET is_active=false,retired_by=@p1,retired_at=now() WHERE id=@p0", ct, tariffId, Actor);
        var after = (await Rows($"SELECT {PriceFields} FROM eport_tariffs WHERE id=@p0", ct, tariffId)).Single();
        await Audit(id, "TARIFF_RETIRED", before, after, ct); await tx.CommitAsync(ct); return after;
    }
    public async Task<CatalogQuote> Quote(Guid id, QuoteInput input, CancellationToken ct)
    {
        await Authorize(true, ct);
        var service = await Find(id, ct);
        var prices = await Rows($"SELECT {PriceFields} FROM eport_tariffs WHERE service_id=@p0 AND is_active", ct, id);
        return CatalogRules.Quote(service["isActive"]!.GetValue<bool>(), service["unit"]!.ToString(), prices.Select(r => new PriceOption(Guid.Parse(r["id"]!.ToString()), r["size"]!.ToString(), r["equipment"]!.ToString(), r["amount"]!.GetValue<decimal>(), r["effectiveFrom"]!.ToString(), r["effectiveTo"]?.ToString(), true)), input);
    }
    public async Task<object> History(Guid id, CancellationToken ct)
    {
        await Authorize(true, ct); await Find(id, ct);
        return await Rows("SELECT id,action,before_data AS \"before\",after_data AS \"after\",created_at AS \"createdAt\" FROM eport_catalog_audit WHERE entity_id=@p0 ORDER BY created_at DESC LIMIT 100", ct, id);
    }
}
