using System.Globalization;
using System.Text.RegularExpressions;

namespace NexusPort.Modules.ServiceCatalog.Domain;

public sealed record ServiceDraftSource
{
    public Guid? BookingId { get; init; }
    public string DocumentType { get; init; } = "EDO";
    public string ReferenceNumber { get; init; } = "";
    public string BlNumber { get; init; } = "";
    public string? ValidUntil { get; init; }
    public string BookingMode { get; init; } = "LINKED";
    public string VesselName { get; init; } = "";
    public string VoyageNumber { get; init; } = "";
}

public sealed record ServiceDraftUsage
{
    public Guid ServiceId { get; init; }
    public string? StartsAt { get; init; }
    public string? EndsAt { get; init; }
    public decimal? Quantity { get; init; }
}

public sealed record ServiceDraftLine
{
    // An export_booking_containers occurrence or container_visits occurrence;
    // never a physical container ID used to guess its latest operational stay.
    public Guid SourceContainerId { get; init; }
    public ServiceDraftUsage[] Services { get; init; } = [];
    public bool CustomsSealRequired { get; init; }
    public ServiceContainerDeclaration? Declaration { get; init; }
}

public sealed record ServiceDraftInput
{
    public int ExpectedVersion { get; init; }
    public string Flow { get; init; } = "EXPORT";
    public Guid? MainServiceId { get; init; }
    public ServiceDraftSource Source { get; init; } = new();
    public string? PlannedAt { get; init; }
    public string ContactName { get; init; } = "";
    public string ContactPhone { get; init; } = "";
    public string Note { get; init; } = "";
    public ServiceDraftLine[] Lines { get; init; } = [];
    public int WorkflowVersion { get; init; }
    public ServiceDeliveryInput Delivery { get; init; } = new();
    public ServiceInvoiceBuyer InvoiceBuyer { get; init; } = new();
}

public static class ServiceDraftRules
{
    public static void Require(bool condition, string message, int status = 422, string code = "SERVICE_DRAFT_VALIDATION")
    { if (!condition) throw new CatalogException(message, status, code); }

    private static string Text(string? value, int limit, string label)
    {
        var text = (value ?? "").Trim();
        Require(text.Length <= limit, $"{label}: tối đa {limit} ký tự.");
        Require(!text.Contains('\0'), $"{label} chứa ký tự không hợp lệ.");
        return text;
    }

    public static string Flow(string? flow)
    {
        Require(flow is "EXPORT" or "IMPORT", "Chọn luồng hàng xuất hoặc hàng nhập.");
        return flow!;
    }

    public static string Search(string? search) => Text(search, 100, "Từ khóa");

    private static string? Instant(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        Require(value.Length <= 45 && Regex.IsMatch(value, @"^\d{4}-\d{2}-\d{2}T.+(?:Z|[+-]\d{2}:\d{2})$"), $"{label} phải có múi giờ (Z hoặc +07:00).");
        Require(DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed), $"{label} không hợp lệ.");
        return parsed.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    public static ServiceDraftInput Normalize(ServiceDraftInput input)
    {
        Require(input.ExpectedVersion >= 0, "Phiên bản bản nháp không hợp lệ.");
        Require(input.WorkflowVersion is 0 or 2, "Phiên bản quy trình không hợp lệ.");
        var flow = Flow(input.Flow);
        Require(input.MainServiceId != Guid.Empty, "Dịch vụ chính không hợp lệ.");
        var source = input.Source ?? new ServiceDraftSource();
        Require(source.BookingId != Guid.Empty, "Booking không hợp lệ.");
        Require(source.DocumentType is "EDO" or "DO", "Loại chứng từ phải là DO hoặc eDO.");
        string? validUntil = null;
        if (!string.IsNullOrWhiteSpace(source.ValidUntil))
        {
            Require(DateOnly.TryParseExact(source.ValidUntil, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date), "Hạn DO/eDO phải là ngày hợp lệ (yyyy-MM-dd).");
            validUntil = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        Require(flow != "IMPORT" || source.BookingId == null, "Luồng nhập dùng tham chiếu DO/eDO, không dùng Booking xuất.");
        Require(source.BookingMode is "LINKED" or "PAPER", "Chọn Booking điện tử hoặc Booking giấy.");
        source = source with
        {
            ReferenceNumber = Text(source.ReferenceNumber, 100, "Số DO/eDO"),
            BlNumber = Text(source.BlNumber, 100, "Số vận đơn"),
            VesselName = Text(source.VesselName, 150, "Tên tàu"),
            VoyageNumber = Text(source.VoyageNumber, 80, "Chuyến tàu"),
            ValidUntil = validUntil
        };
        var lines = input.Lines ?? [];
        Require(lines.Length <= 50 && lines.All(line => line != null && line.SourceContainerId != Guid.Empty), "Bản nháp tối đa 50 container hợp lệ.");
        Require(lines.Select(line => line.SourceContainerId).Distinct().Count() == lines.Length, "Container bị chọn trùng trong bản nháp.");
        var normalized = lines.Select(line =>
        {
            var usages = line.Services ?? [];
            Require(usages.Length <= 12 && usages.All(usage => usage != null && usage.ServiceId != Guid.Empty), "Mỗi container tối đa 12 dịch vụ hợp lệ.");
            Require(usages.Select(usage => usage.ServiceId).Distinct().Count() == usages.Length, "Dịch vụ bị chọn trùng trên cùng container.");
            usages = usages.Select(usage =>
            {
                var start = Instant(usage.StartsAt, "Thời gian bắt đầu");
                var end = Instant(usage.EndsAt, "Thời gian kết thúc");
                Require(start == null || end == null || string.CompareOrdinal(end, start) > 0, "Thời gian kết thúc phải sau thời gian bắt đầu.");
                Require(usage.Quantity == null || (usage.Quantity > 0 && usage.Quantity <= 10000 && decimal.Round(usage.Quantity.Value, 2) == usage.Quantity), "Số lượng phải dương, tối đa 10.000 và không quá hai số thập phân.");
                return usage with { StartsAt = start, EndsAt = end };
            }).ToArray();
            if (input.MainServiceId.HasValue && usages.All(usage => usage.ServiceId != input.MainServiceId.Value))
                usages = [new ServiceDraftUsage { ServiceId = input.MainServiceId.Value }, .. usages];
            Require(usages.Length <= 12, "Mỗi container tối đa 12 dịch vụ bao gồm dịch vụ chính.");
            return line with { Services = usages, Declaration = ServiceDeliveryRules.NormalizeDeclaration(line.Declaration) };
        }).ToArray();
        return input with
        {
            Flow = flow, Source = source, Lines = normalized,
            PlannedAt = Instant(input.PlannedAt, "Ngày dự kiến thực hiện"),
            ContactName = Text(input.ContactName, 120, "Tên người liên hệ"),
            ContactPhone = Text(input.ContactPhone, 30, "Số điện thoại"),
            Note = Text(input.Note, 1000, "Ghi chú")
            ,Delivery = ServiceDeliveryRules.Normalize(input.Delivery), InvoiceBuyer = ServiceDeliveryRules.NormalizeBuyer(input.InvoiceBuyer)
        };
    }
}
