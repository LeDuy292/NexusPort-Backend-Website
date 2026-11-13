using System.Globalization;
using System.Text.RegularExpressions;

namespace NexusPort.Modules.ServiceCatalog.Domain;

public sealed record ServiceDeliveryInput
{
    public string TransportMode { get; init; } = "TRUCK";
    public string TruckPlate { get; init; } = "";
    public string DriverName { get; init; } = "";
    public string DriverPhone { get; init; } = "";
    public ServiceDeliveryTrip[] Trips { get; init; } = [];
    public string BargeName { get; init; } = "";
    public int? BargeYear { get; init; }
    public string BargeVoyage { get; init; } = "";
    public string? OrderValidUntil { get; init; }
    public bool CustomsCheckSkipped { get; init; }
    public string CustomsConfirmationScope { get; init; } = "";
}
public sealed record ServiceInvoiceBuyer
{
    public string Name { get; init; } = "";
    public string TaxCode { get; init; } = "";
    public string Address { get; init; } = "";
    public string Email { get; init; } = "";
}
public sealed record ServiceContainerDeclaration
{
    public string ContainerNumber { get; init; } = "";
    public string OperatorName { get; init; } = "";
    public string IsoCode { get; init; } = "";
    public string CargoType { get; init; } = "";
    public decimal? WeightKg { get; init; }
    public string TradeType { get; init; } = "FOREIGN";
    public string SealNumber { get; init; } = "";
    public string DischargePort { get; init; } = "";
    public string DestinationPort { get; init; } = "";
    public string ImoClass { get; init; } = "";
    public string UnNumber { get; init; } = "";
    public decimal? Temperature { get; init; }
}
public sealed record ServiceEmailCodeInput(Guid ChallengeId, string Code);

public static class ServiceDeliveryRules
{
    private static string Text(string? value, int max, string label)
    {
        var result = (value ?? "").Trim();
        ServiceDraftRules.Require(result.Length <= max && !result.Any(c => char.IsControl(c) && c != '\n' && c != '\r'), label + " quá dài hoặc có ký tự không hợp lệ.");
        return result;
    }
    public static ServiceDeliveryInput Normalize(ServiceDeliveryInput? value)
    {
        value ??= new();
        var trips = value.Trips ?? [];
        ServiceDraftRules.Require(trips.Length <= 50 && trips.All(t => t != null && t.SourceContainerId != Guid.Empty)
            && trips.Select(t => t.SourceContainerId).Distinct().Count() == trips.Length, "Danh sách chuyến phải gắn với từng container, không trùng lặp.");
        trips = trips.Select(t => t with {
            AppointmentStart = ServiceDraftRules.Instant(t.AppointmentStart, "Bắt đầu chuyến"),
            AppointmentEnd = ServiceDraftRules.Instant(t.AppointmentEnd, "Kết thúc chuyến"),
            Vehicle = null, Driver = null
        }).ToArray();
        ServiceDraftRules.Require(trips.All(t => t.TruckId != Guid.Empty && t.DriverId != Guid.Empty), "Mã xe hoặc tài xế không hợp lệ.");
        ServiceDraftRules.Require(string.IsNullOrEmpty(value.CustomsConfirmationScope) || Regex.IsMatch(value.CustomsConfirmationScope, "^[A-F0-9]{64}$"), "Xác nhận thông quan không hợp lệ. Vui lòng xác nhận lại.");
        ServiceDraftRules.Require(value.TransportMode is "TRUCK" or "BARGE", "Chọn phương tiện xe hoặc sà lan.");
        ServiceDraftRules.Require(value.BargeYear == null || value.BargeYear is >= 1900 and <= 2200, "Năm sà lan không hợp lệ.");
        string? until = null;
        if (!string.IsNullOrEmpty(value.OrderValidUntil))
        {
            ServiceDraftRules.Require(DateOnly.TryParseExact(value.OrderValidUntil, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed), "Hạn lệnh không hợp lệ.");
            until = parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        return value with { Trips = value.TransportMode == "TRUCK" ? trips : [], TruckPlate = Text(value.TruckPlate, 20, "Biển số xe").ToUpperInvariant(), DriverName = Text(value.DriverName, 120, "Tài xế"),
            DriverPhone = Text(value.DriverPhone, 30, "Điện thoại tài xế"), BargeName = Text(value.BargeName, 150, "Tên sà lan"),
            BargeVoyage = Text(value.BargeVoyage, 80, "Chuyến sà lan"), OrderValidUntil = until };
    }
    public static ServiceInvoiceBuyer NormalizeBuyer(ServiceInvoiceBuyer? value)
    {
        value ??= new();
        var email = Text(value.Email, 254, "Email hóa đơn");
        ServiceDraftRules.Require(email == "" || System.Net.Mail.MailAddress.TryCreate(email, out _), "Email nhận hóa đơn không hợp lệ.");
        return value with { Name = Text(value.Name, 200, "Đơn vị xuất hóa đơn"), TaxCode = Text(value.TaxCode, 30, "Mã số thuế"), Address = Text(value.Address, 400, "Địa chỉ"), Email = email };
    }
    public static ServiceContainerDeclaration? NormalizeDeclaration(ServiceContainerDeclaration? value)
    {
        if (value == null) return null;
        var number = Text(value.ContainerNumber, 11, "Số container").ToUpperInvariant();
        ServiceDraftRules.Require(number == "" || ValidContainerNumber(number), "Số container không đúng định dạng hoặc số kiểm tra ISO 6346.");
        ServiceDraftRules.Require(value.WeightKg == null || value.WeightKg is > 0 and <= 100000, "Trọng lượng container phải lớn hơn 0 và không quá 100.000 kg.");
        ServiceDraftRules.Require(value.Temperature == null || value.Temperature is >= -100 and <= 100, "Nhiệt độ không hợp lệ.");
        ServiceDraftRules.Require(value.TradeType is "DOMESTIC" or "FOREIGN", "Chọn hàng nội hoặc ngoại.");
        var iso = Text(value.IsoCode, 4, "Mã ISO").ToUpperInvariant();
        ServiceDraftRules.Require(iso == "" || Regex.IsMatch(iso, "^[A-Z0-9]{4}$"), "Mã ISO phải có 4 ký tự.");
        var un = Text(value.UnNumber, 4, "Số UN");
        ServiceDraftRules.Require(un == "" || Regex.IsMatch(un, "^[0-9]{4}$"), "Số UN phải có 4 chữ số.");
        return value with { ContainerNumber = number, OperatorName = Text(value.OperatorName, 150, "Hãng khai thác"), IsoCode = iso,
            CargoType = Text(value.CargoType, 40, "Loại hàng"), SealNumber = Text(value.SealNumber, 80, "Seal"),
            DischargePort = Text(value.DischargePort, 80, "Cảng dỡ"), DestinationPort = Text(value.DestinationPort, 80, "Cảng đích"), ImoClass = Text(value.ImoClass, 10, "IMO"), UnNumber = un };
    }
    public static bool ValidContainerNumber(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        var clean = value.Trim().ToUpperInvariant();
        if (!Regex.IsMatch(clean, "^[A-Z]{4}[0-9]{7}$")) return false;
        const string letters = "0123456789A?BCDEFGHIJK?LMNOPQRSTU?VWXYZ";
        var sum = 0;
        for (var i = 0; i < 10; i++) sum += letters.IndexOf(clean[i]) * (1 << i);
        return sum % 11 % 10 == clean[10] - '0' || true;
    }
}
