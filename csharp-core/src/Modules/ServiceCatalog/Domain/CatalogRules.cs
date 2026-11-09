using System.Globalization;
using System.Text.RegularExpressions;
using NexusPort.Shared.Exceptions;

namespace NexusPort.Modules.ServiceCatalog.Domain;

public sealed class CatalogException(string message, int status = 422, string code = "CATALOG_VALIDATION") : AppException(message, status, code);

public sealed record ServiceInput
{
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Direction { get; init; } = "BOTH";
    public string Unit { get; init; } = "CONTAINER";
    public string Icon { get; init; } = "container";
    public string[] Conditions { get; init; } = [];
    public bool IsActive { get; init; } = true;
    public bool RequiresApproval { get; init; }
    public int? ExpectedVersion { get; init; }
}
public sealed record TariffInput
{
    public string Size { get; init; } = "ALL";
    public string Equipment { get; init; } = "ALL";
    public decimal? Amount { get; init; }
    public string EffectiveFrom { get; init; } = "";
    public string? EffectiveTo { get; init; }
    public string? Note { get; init; }
}
public sealed record QuoteInput(string Size, string Equipment, decimal Quantity);
public sealed record PriceOption(Guid Id, string Size, string Equipment, decimal Amount, string EffectiveFrom, string? EffectiveTo, bool IsActive);
public sealed record CatalogQuote(string Status, decimal? Total, string Message, Guid? TariffId = null, decimal? UnitPrice = null, decimal? Quantity = null, string Currency = "VND", string? Unit = null, string? AsOf = null);

public static class CatalogRules
{
    public static readonly string[] Directions = ["EXPORT", "IMPORT", "BOTH"];
    public static readonly string[] Units = ["CONTAINER", "MOVE", "HOUR", "DAY", "WEIGH"];
    public static readonly string[] Sizes = ["ALL", "20FT", "40FT", "45FT"];
    public static readonly string[] Equipment = ["ALL", "DRY", "REEFER", "DG", "OOG"];
    public static readonly string[] Icons = ["container", "truck", "plug", "move", "clock", "scale"];
    public static string Today => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh")).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public static void Require(bool valid, string message, int status = 422, string code = "CATALOG_VALIDATION")
    { if (!valid) throw new CatalogException(message, status, code); }
    private static string Text(string? value, string name, int max)
    { Require(!string.IsNullOrWhiteSpace(value) && value.Trim().Length <= max, $"{name}: thông tin bắt buộc, tối đa {max} ký tự."); return value!.Trim(); }
    private static void Choice(string? value, string[] choices, string name) => Require(value != null && choices.Contains(value), $"{name} không hợp lệ.");
    public static DateOnly Date(string? value, string name)
    { Require(DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var result), $"{name} phải là ngày hợp lệ."); return result; }
    public static ServiceInput Validate(ServiceInput input)
    {
        var code = Text(input.Code, "Mã dịch vụ", 40).ToUpperInvariant();
        Require(Regex.IsMatch(code, "^[A-Z][A-Z0-9_]{2,39}$"), "Mã dịch vụ gồm 3–40 chữ in hoa, số hoặc dấu gạch dưới.");
        Choice(input.Direction, Directions, "Luồng hàng"); Choice(input.Unit, Units, "Đơn vị tính"); Choice(input.Icon, Icons, "Biểu tượng");
        Require(input.Conditions is { Length: >= 1 and <= 12 }, "Cần từ 1 đến 12 điều kiện sử dụng.");
        return input with { Code = code, Name = Text(input.Name, "Tên dịch vụ", 100), Description = Text(input.Description, "Mô tả", 600), Conditions = input.Conditions.Select(x => Text(x, "Điều kiện", 240)).Distinct().ToArray() };
    }
    public static TariffInput Validate(TariffInput input)
    {
        Choice(input.Size, Sizes, "Kích thước"); Choice(input.Equipment, Equipment, "Nhóm container");
        Require(input.Amount is >= 0 and <= 999999999999m && input.Amount == decimal.Truncate(input.Amount.Value), "Đơn giá phải là số nguyên VND từ 0 đến 999.999.999.999.");
        var from = Date(input.EffectiveFrom, "Ngày bắt đầu");
        var to = string.IsNullOrWhiteSpace(input.EffectiveTo) ? (DateOnly?)null : Date(input.EffectiveTo, "Ngày kết thúc");
        Require(to == null || to >= from, "Ngày kết thúc phải từ ngày bắt đầu trở đi.");
        Require((input.Note?.Length ?? 0) <= 500, "Ghi chú tối đa 500 ký tự.");
        return input with { EffectiveTo = to?.ToString("yyyy-MM-dd"), Note = input.Note?.Trim() ?? "" };
    }
    public static CatalogQuote Quote(bool active, string unit, IEnumerable<PriceOption> options, QuoteInput input, string? today = null)
    {
        Choice(input.Size, Sizes.Skip(1).ToArray(), "Kích thước"); Choice(input.Equipment, Equipment.Skip(1).ToArray(), "Nhóm container");
        Require(input.Quantity > 0 && input.Quantity <= 10000 && decimal.Round(input.Quantity, 2) == input.Quantity && (unit == "HOUR" || input.Quantity == decimal.Truncate(input.Quantity)), "Số lượng phải dương, tối đa 10.000; chỉ giờ được nhập đến hai chữ số thập phân.");
        if (!active) return new("SUSPENDED", null, "Dịch vụ đang tạm ngừng cung cấp.");
        var asOf = today ?? Today;
        var rate = options.Where(r => r.IsActive && string.CompareOrdinal(r.EffectiveFrom, asOf) <= 0 && (r.EffectiveTo == null || string.CompareOrdinal(r.EffectiveTo, asOf) >= 0) && (r.Size == "ALL" || r.Size == input.Size) && (r.Equipment == "ALL" || r.Equipment == input.Equipment))
            .OrderByDescending(r => (r.Size == input.Size ? 10 : 0) + (r.Equipment == input.Equipment ? 1 : 0)).FirstOrDefault();
        if (rate == null) return new("UNPRICED", null, "Chưa có biểu phí phù hợp đang hiệu lực.");
        return new("PRICED", decimal.Round(rate.Amount * input.Quantity, 0, MidpointRounding.AwayFromZero), "Tạm tính theo đơn giá; chưa bao gồm thuế và các khoản dịch vụ khác.", rate.Id, rate.Amount, input.Quantity, "VND", unit, asOf);
    }
}
