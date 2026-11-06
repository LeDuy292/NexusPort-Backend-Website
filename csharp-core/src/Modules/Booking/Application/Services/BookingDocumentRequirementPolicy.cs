using NexusPort.Modules.Booking.Application.DTOs;

namespace NexusPort.Modules.Booking.Application.Services;

public class DocumentRequirementSpec
{
    public string DocumentType { get; set; } = string.Empty;
    public string NameVi { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsMandatory { get; set; }
    public string ApplicableStage { get; set; } = "PreGate";
}

public interface IBookingDocumentRequirementPolicy
{
    IReadOnlyList<DocumentRequirementSpec> GetRequiredDocuments(string operationType, string cargoStatus, string cargoType);
    (bool IsValid, List<string> MissingMandatoryDocuments) ValidateUploadedDocuments(
        string operationType,
        string cargoStatus,
        string cargoType,
        IReadOnlyList<string> uploadedDocumentTypes);
}

public class BookingDocumentRequirementPolicy : IBookingDocumentRequirementPolicy
{
    public IReadOnlyList<DocumentRequirementSpec> GetRequiredDocuments(string operationType, string cargoStatus, string cargoType)
    {
        var list = new List<DocumentRequirementSpec>();
        var op = operationType?.ToLowerInvariant() ?? "";
        var isLoaded = string.Equals(cargoStatus, "Loaded", StringComparison.OrdinalIgnoreCase);
        var isReefer = string.Equals(cargoType, "Reefer", StringComparison.OrdinalIgnoreCase) ||
                       cargoType?.Contains("Lạnh", StringComparison.OrdinalIgnoreCase) == true;
        var isDg = string.Equals(cargoType, "DangerousGoods", StringComparison.OrdinalIgnoreCase) ||
                   cargoType?.Contains("Nguy hiểm", StringComparison.OrdinalIgnoreCase) == true;

        switch (op)
        {
            case "export":
                list.Add(new DocumentRequirementSpec
                {
                    DocumentType = "CarrierBookingReference",
                    NameVi = "Mã đặt chỗ Hãng tàu (Booking Note)",
                    Description = "Chứng từ xác nhận số booking xuất hàng từ Hãng tàu quốc tế",
                    IsMandatory = true
                });
                list.Add(new DocumentRequirementSpec
                {
                    DocumentType = "VgmCertificate",
                    NameVi = "Phiếu xác nhận khối lượng toàn bộ (VGM)",
                    Description = "Chứng thư cân tải trọng container theo quy chuẩn SOLAS",
                    IsMandatory = isLoaded
                });
                list.Add(new DocumentRequirementSpec
                {
                    DocumentType = "CustomsDeclaration",
                    NameVi = "Tờ khai Hải quan xuất khẩu",
                    Description = "Thông quan tờ khai xuất khẩu hoặc danh sách container đủ điều kiện qua khu vực giám sát hải quan",
                    IsMandatory = false
                });
                break;

            case "import":
                list.Add(new DocumentRequirementSpec
                {
                    DocumentType = "BillOfLading",
                    NameVi = "Vận đơn đường biển (Bill of Lading - B/L)",
                    Description = "Vận đơn chủ (MBL) hoặc vận đơn thứ cấp (HBL) chứng minh quyền nhận hàng",
                    IsMandatory = true
                });
                list.Add(new DocumentRequirementSpec
                {
                    DocumentType = "DeliveryOrder",
                    NameVi = "Lệnh giao hàng điện tử (eDO)",
                    Description = "Lệnh giao hàng từ hãng tàu/đại lý vận tải cấp cho chủ hàng",
                    IsMandatory = true
                });
                list.Add(new DocumentRequirementSpec
                {
                    DocumentType = "CustomsDeclaration",
                    NameVi = "Tờ khai Hải quan thông quan nhập khẩu",
                    Description = "Xác nhận hàng hóa đã hoàn tất nghĩa vụ thuế và đủ điều kiện giải phóng hàng khỏi cảng",
                    IsMandatory = true
                });
                break;
        }

        // Tài liệu đặc biệt cho hàng Lạnh (Reefer)
        if (isReefer)
        {
            list.Add(new DocumentRequirementSpec
            {
                DocumentType = "ReeferTemperatureInstructions",
                NameVi = "Phiếu cài đặt nhiệt độ & bảo quản hàng lạnh",
                Description = "Hướng dẫn cài đặt nhiệt độ (Set Point), thông gió và cắm điện kho lạnh cảng",
                IsMandatory = true
            });
        }

        // Tài liệu đặc biệt cho hàng Nguy hiểm (Dangerous Goods)
        if (isDg)
        {
            list.Add(new DocumentRequirementSpec
            {
                DocumentType = "DangerousGoodsDeclaration",
                NameVi = "Bản khai báo hàng hóa nguy hiểm (IMO / DG Declaration)",
                Description = "Khai báo phân lớp hàng nguy hiểm (DG Class), mã số UN và giấy phép lưu bãi cảng",
                IsMandatory = true
            });
        }

        return list;
    }

    public (bool IsValid, List<string> MissingMandatoryDocuments) ValidateUploadedDocuments(
        string operationType,
        string cargoStatus,
        string cargoType,
        IReadOnlyList<string> uploadedDocumentTypes)
    {
        var required = GetRequiredDocuments(operationType, cargoStatus, cargoType);
        var uploadedSet = new HashSet<string>(uploadedDocumentTypes ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

        bool HasDocument(string reqDocType)
        {
            if (uploadedSet.Contains(reqDocType)) return true;
            if (reqDocType.Equals("CarrierBookingReference", StringComparison.OrdinalIgnoreCase) && uploadedSet.Contains("BookingNote")) return true;
            if (reqDocType.Equals("BookingNote", StringComparison.OrdinalIgnoreCase) && uploadedSet.Contains("CarrierBookingReference")) return true;
            if (reqDocType.Equals("ReeferTemperatureInstructions", StringComparison.OrdinalIgnoreCase) && uploadedSet.Contains("ReeferInstructions")) return true;
            if (reqDocType.Equals("ReeferInstructions", StringComparison.OrdinalIgnoreCase) && uploadedSet.Contains("ReeferTemperatureInstructions")) return true;
            return false;
        }

        var missing = required
            .Where(r => r.IsMandatory && !HasDocument(r.DocumentType))
            .Select(r => $"{r.NameVi} ({r.DocumentType})")
            .ToList();

        return (missing.Count == 0, missing);
    }
}
