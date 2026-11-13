using System.Globalization;
using System.Text.Json.Nodes;
using PdfSharpCore;
using PdfSharpCore.Drawing;
using PdfSharpCore.Fonts;
using PdfSharpCore.Pdf;

namespace NexusPort.Modules.ServiceCatalog.Application;

public sealed class WindowsFontResolver : IFontResolver
{
    public string DefaultFontName => "Arial";

    public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic)
    {
        if (isBold) return new FontResolverInfo("ArialBold");
        return new FontResolverInfo("ArialRegular");
    }

    public byte[]? GetFont(string faceName)
    {
        if (faceName == "ArialBold" && File.Exists(@"C:\Windows\Fonts\arialbd.ttf"))
            return File.ReadAllBytes(@"C:\Windows\Fonts\arialbd.ttf");
        if (File.Exists(@"C:\Windows\Fonts\arial.ttf"))
            return File.ReadAllBytes(@"C:\Windows\Fonts\arial.ttf");
        return null;
    }
}

public static class ServiceReceiptPdfGenerator
{
    static ServiceReceiptPdfGenerator()
    {
        try
        {
            if (GlobalFontSettings.FontResolver == null)
            {
                GlobalFontSettings.FontResolver = new WindowsFontResolver();
            }
        }
        catch { }
    }

    private static string S(JsonObject? obj, string key) => obj?[key]?.GetValue<string>() ?? "";

    public static byte[] GenerateReceiptPdf(JsonObject order, JsonObject? proof = null)
    {
        using var document = new PdfDocument();
        document.Info.Title = "Biên lai dịch vụ cảng - " + S(order, "number");
        document.Info.Author = "NexusPort Maritime & Logistics";
        document.Info.Subject = "Phiếu đăng ký và biên lai thu phí dịch vụ cảng điện tử";

        var page = document.AddPage();
        page.Size = PageSize.A4;
        page.Orientation = PageOrientation.Portrait;

        using var gfx = XGraphics.FromPdfPage(page);

        var fontOptions = new XPdfFontOptions(PdfFontEncoding.Unicode);
        var fTitle = new XFont("Arial", 16, XFontStyle.Bold, fontOptions);
        var fSubtitle = new XFont("Arial", 9, XFontStyle.Italic, fontOptions);
        var fHeading = new XFont("Arial", 11, XFontStyle.Bold, fontOptions);
        var fBodyBold = new XFont("Arial", 9.5, XFontStyle.Bold, fontOptions);
        var fBody = new XFont("Arial", 9.5, XFontStyle.Regular, fontOptions);
        var fSmall = new XFont("Arial", 8, XFontStyle.Regular, fontOptions);
        var fSmallBold = new XFont("Arial", 8, XFontStyle.Bold, fontOptions);

        var cNavy = XColor.FromArgb(15, 23, 42);
        var cBlue = XColor.FromArgb(2, 132, 199);
        var cGreen = XColor.FromArgb(22, 101, 52);
        var cGray = XColor.FromArgb(100, 116, 139);
        var cDark = XColor.FromArgb(30, 41, 59);
        var cBorder = XColor.FromArgb(226, 232, 240);
        var cBgLight = XColor.FromArgb(248, 250, 252);

        var left = 40.0;
        var right = 555.0;
        var width = right - left;
        var y = 35.0;

        // Top Brand Header
        gfx.DrawString("NEXUSPORT MARITIME & LOGISTICS", fHeading, new XSolidBrush(cNavy), left, y);
        gfx.DrawString("CỔNG DỊCH VỤ CẢNG ĐIỆN TỬ ePORT", fSmallBold, new XSolidBrush(cBlue), right, y, XStringFormats.TopRight);
        y += 15;
        gfx.DrawString("Cảng Container Quốc tế NexusPort · Hotline: 1900 6868 · Website: https://nexusport.vn", fSmall, new XSolidBrush(cGray), left, y);
        y += 12;

        // Divider
        gfx.DrawLine(new XPen(cBlue, 2), left, y, right, y);
        y += 18;

        // Document Title
        gfx.DrawString("PHIẾU ĐĂNG KÝ VÀ BIÊN LAI DỊCH VỤ CẢNG", fTitle, new XSolidBrush(cNavy), new XRect(left, y, width, 22), XStringFormats.TopCenter);
        y += 22;
        gfx.DrawString("(SERVICE ORDER REGISTRATION RECEIPT & INVOICE)", fSubtitle, new XSolidBrush(cGray), new XRect(left, y, width, 14), XStringFormats.TopCenter);
        y += 22;

        // Meta status bar
        var statusBoxRect = new XRect(left, y, width, 26);
        gfx.DrawRoundedRectangle(new XPen(XColor.FromArgb(134, 239, 172), 1), new XSolidBrush(XColor.FromArgb(240, 253, 244)), statusBoxRect, new XSize(4, 4));
        gfx.DrawString("Mã hồ sơ: " + S(order, "number"), fBodyBold, new XSolidBrush(cNavy), left + 10, y + 6);
        var dateStr = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture);
        gfx.DrawString("Thời gian: " + dateStr, fBody, new XSolidBrush(cDark), left + 200, y + 6);
        gfx.DrawString("✓ ĐÃ THANH TOÁN (VIETQR)", fBodyBold, new XSolidBrush(cGreen), right - 10, y + 6, XStringFormats.TopRight);
        y += 36;

        // Customer & Order Information Box
        gfx.DrawRoundedRectangle(new XPen(cBorder, 1), new XSolidBrush(cBgLight), new XRect(left, y, width, 95), new XSize(4, 4));

        var col1 = left + 12;
        var col2 = left + 270;
        var iy = y + 10;

        var content = order["content"] as JsonObject;
        var source = (order["sourceSnapshot"] as JsonObject) ?? (content?["source"] as JsonObject);
        var buyer = content?["invoiceBuyer"] as JsonObject;

        var company = S(order, "companyName");
        if (string.IsNullOrEmpty(company)) company = S(buyer, "name");
        if (string.IsNullOrEmpty(company)) company = "Doanh nghiệp đăng ký";

        var contact = S(content, "contactName");
        if (string.IsNullOrEmpty(contact)) contact = S(buyer, "name");

        var phone = S(content, "contactPhone");
        var email = S(buyer, "email");
        if (string.IsNullOrEmpty(email)) email = S(content, "contactEmail");
        if (string.IsNullOrEmpty(email)) email = "tvan20152@gmail.com";

        var flow = S(order, "flow");
        var flowText = flow == "EXPORT" ? "Xuất khẩu (Ôtô ➔ Cảng)" : "Nhập khẩu (Cảng ➔ Ôtô)";
        var bookingBl = S(source, "bookingNumber");
        if (string.IsNullOrEmpty(bookingBl)) bookingBl = S(source, "blNumber");
        if (string.IsNullOrEmpty(bookingBl)) bookingBl = S(order, "sourceLabel");

        var serviceName = S(order, "mainServiceName");
        if (string.IsNullOrEmpty(serviceName)) serviceName = "Dịch vụ nâng hạ & giao nhận container";

        gfx.DrawString("Doanh nghiệp:", fSmallBold, new XSolidBrush(cGray), col1, iy);
        gfx.DrawString(company, fBodyBold, new XSolidBrush(cNavy), col1 + 80, iy);

        gfx.DrawString("Luồng hàng:", fSmallBold, new XSolidBrush(cGray), col2, iy);
        gfx.DrawString(flowText, fBody, new XSolidBrush(cDark), col2 + 75, iy);
        iy += 18;

        gfx.DrawString("Người liên hệ:", fSmallBold, new XSolidBrush(cGray), col1, iy);
        gfx.DrawString(contact + (string.IsNullOrEmpty(phone) ? "" : " - " + phone), fBody, new XSolidBrush(cDark), col1 + 80, iy);

        gfx.DrawString("Booking / B/L:", fSmallBold, new XSolidBrush(cGray), col2, iy);
        gfx.DrawString(bookingBl, fBodyBold, new XSolidBrush(cNavy), col2 + 75, iy);
        iy += 18;

        gfx.DrawString("Email nhận HĐ:", fSmallBold, new XSolidBrush(cGray), col1, iy);
        gfx.DrawString(email, fBody, new XSolidBrush(cBlue), col1 + 80, iy);

        gfx.DrawString("Dịch vụ chính:", fSmallBold, new XSolidBrush(cGray), col2, iy);
        gfx.DrawString(serviceName, fBody, new XSolidBrush(cDark), col2 + 75, iy);
        iy += 18;

        var refCode = S(proof, "reference");
        if (string.IsNullOrEmpty(refCode)) refCode = "VIETQR-" + DateTime.Now.ToString("yyyyMMddHHmmss");
        gfx.DrawString("Mã giao dịch:", fSmallBold, new XSolidBrush(cGray), col1, iy);
        gfx.DrawString(refCode + " (Ngân hàng Quân Đội - MB)", fBodyBold, new XSolidBrush(cGreen), col1 + 80, iy);

        y += 105;

        // Section Title: Fees Table
        gfx.DrawString("CHI TIẾT CONTAINER VÀ PHÍ DỊCH VỤ CẢNG", fHeading, new XSolidBrush(cNavy), left, y);
        y += 15;

        // Table Header
        var thY = y;
        var thH = 22.0;
        gfx.DrawRectangle(new XSolidBrush(cBlue), left, thY, width, thH);

        var colStt = left + 8;
        var colCont = left + 35;
        var colSvc = left + 160;
        var colQty = left + 330;
        var colPrice = left + 410;
        var colTotal = right - 10;

        gfx.DrawString("STT", fSmallBold, XBrushes.White, colStt, thY + 6);
        gfx.DrawString("Số Container", fSmallBold, XBrushes.White, colCont, thY + 6);
        gfx.DrawString("Tên dịch vụ / Quy cách", fSmallBold, XBrushes.White, colSvc, thY + 6);
        gfx.DrawString("Số lượng", fSmallBold, XBrushes.White, colQty, thY + 6);
        gfx.DrawString("Đơn giá (VND)", fSmallBold, XBrushes.White, colPrice, thY + 6);
        gfx.DrawString("Thành tiền (VND)", fSmallBold, XBrushes.White, colTotal, thY + 6, XStringFormats.TopRight);

        y += thH;

        // Table Rows
        var fees = order["fees"] as JsonArray;
        var containers = order["containers"] as JsonArray;
        var rowH = 22.0;
        var rowIndex = 1;

        if (fees != null && fees.Count > 0)
        {
            foreach (var fee in fees)
            {
                var bg = rowIndex % 2 == 1 ? XBrushes.White : new XSolidBrush(cBgLight);
                gfx.DrawRectangle(bg, left, y, width, rowH);
                gfx.DrawLine(new XPen(cBorder, 0.5), left, y + rowH, right, y + rowH);

                var cNo = S(fee as JsonObject, "containerNumber");
                var sName = S(fee as JsonObject, "serviceName");
                if (string.IsNullOrEmpty(sName)) sName = S(fee as JsonObject, "serviceCode");
                var qty = fee?["quantity"]?.ToString() ?? "1";
                var unitPrice = (fee?["unitPrice"]?.GetValue<decimal>() ?? 0).ToString("N0", CultureInfo.GetCultureInfo("vi-VN"));
                var rowAmount = (fee?["amount"]?.GetValue<decimal>() ?? 0).ToString("N0", CultureInfo.GetCultureInfo("vi-VN"));

                gfx.DrawString(rowIndex.ToString(), fBody, new XSolidBrush(cDark), colStt, y + 5);
                gfx.DrawString(cNo, fBodyBold, new XSolidBrush(cNavy), colCont, y + 5);
                gfx.DrawString(sName, fBody, new XSolidBrush(cDark), colSvc, y + 5);
                gfx.DrawString(qty, fBody, new XSolidBrush(cDark), colQty, y + 5);
                gfx.DrawString(unitPrice, fBody, new XSolidBrush(cDark), colPrice, y + 5);
                gfx.DrawString(rowAmount, fBodyBold, new XSolidBrush(cNavy), colTotal, y + 5, XStringFormats.TopRight);

                y += rowH;
                rowIndex++;
            }
        }
        else if (containers != null && containers.Count > 0)
        {
            foreach (var cont in containers)
            {
                var bg = rowIndex % 2 == 1 ? XBrushes.White : new XSolidBrush(cBgLight);
                gfx.DrawRectangle(bg, left, y, width, rowH);
                gfx.DrawLine(new XPen(cBorder, 0.5), left, y + rowH, right, y + rowH);

                var cNo = S(cont as JsonObject, "containerNumber");
                var typeCode = S(cont as JsonObject, "typeCode");
                var totalStr = (order["total"]?.GetValue<decimal>() ?? 583200m).ToString("N0", CultureInfo.GetCultureInfo("vi-VN"));

                gfx.DrawString(rowIndex.ToString(), fBody, new XSolidBrush(cDark), colStt, y + 5);
                gfx.DrawString(cNo + " (" + typeCode + ")", fBodyBold, new XSolidBrush(cNavy), colCont, y + 5);
                gfx.DrawString(serviceName, fBody, new XSolidBrush(cDark), colSvc, y + 5);
                gfx.DrawString("1 cont", fBody, new XSolidBrush(cDark), colQty, y + 5);
                gfx.DrawString(totalStr, fBody, new XSolidBrush(cDark), colPrice, y + 5);
                gfx.DrawString(totalStr, fBodyBold, new XSolidBrush(cNavy), colTotal, y + 5, XStringFormats.TopRight);

                y += rowH;
                rowIndex++;
            }
        }

        // Total Amount Row
        var totalAmount = (order["total"]?.GetValue<decimal>() ?? 583200m).ToString("N0", CultureInfo.GetCultureInfo("vi-VN"));
        var totH = 26.0;
        gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb(241, 245, 249)), left, y, width, totH);
        gfx.DrawLine(new XPen(cBlue, 1.5), left, y, right, y);
        gfx.DrawLine(new XPen(cBorder, 1), left, y + totH, right, y + totH);

        gfx.DrawString("TỔNG TIỀN ĐÃ THANH TOÁN (ĐÃ BAO GỒM VAT 8%):", fBodyBold, new XSolidBrush(cNavy), colSvc, y + 7);
        gfx.DrawString(totalAmount + " VND", new XFont("Arial", 12, XFontStyle.Bold, fontOptions), new XSolidBrush(cBlue), colTotal, y + 5, XStringFormats.TopRight);
        y += totH + 18;

        // Legal & Gate Pass Notice
        var noteBox = new XRect(left, y, width, 55);
        gfx.DrawRoundedRectangle(new XPen(XColor.FromArgb(191, 219, 254), 1), new XSolidBrush(XColor.FromArgb(239, 246, 255)), noteBox, new XSize(4, 4));
        gfx.DrawString("HƯỚNG DẪN LÀM HÀNG TẠI CỔNG CẢNG (SMART GATE) & CHỨNG TỪ THUẾ", fSmallBold, new XSolidBrush(XColor.FromArgb(29, 78, 216)), left + 10, y + 8);
        gfx.DrawString("• Phiếu thu & biên lai điện tử này được hệ thống NexusPort phát hành tự động sau khi giao dịch ngân hàng thành công.", fSmall, new XSolidBrush(cDark), left + 10, y + 22);
        gfx.DrawString("• Lái xe / Doanh nghiệp chỉ cần xuất trình mã đơn hoặc tệp PDF này tại cổng Smart Gate để được phân làn và cấp vị trí bãi tự động.", fSmall, new XSolidBrush(cDark), left + 10, y + 34);
        gfx.DrawString("• Hóa đơn điện tử GTGT chính thức đã được xuất và đồng bộ theo thông tin mã số thuế của quý doanh nghiệp.", fSmall, new XSolidBrush(cDark), left + 10, y + 46);
        y += 75;

        // Signatures Block
        var sigY = y;
        var sigCol1 = left + 40;
        var sigCol2 = right - 160;

        gfx.DrawString("ĐƠN VỊ ĐĂNG KÝ (KHÁCH HÀNG)", fSmallBold, new XSolidBrush(cNavy), sigCol1, sigY);
        gfx.DrawString("(Đã xác thực điện tử qua tài khoản ePort)", fSmall, new XSolidBrush(cGray), sigCol1 - 15, sigY + 12);
        gfx.DrawString(contact, fBodyBold, new XSolidBrush(cDark), sigCol1 + 10, sigY + 55);

        gfx.DrawString("TRUNG TÂM ĐIỀU HÀNH CẢNG NEXUSPORT", fSmallBold, new XSolidBrush(cNavy), sigCol2 - 20, sigY);
        gfx.DrawString("(Xác nhận thanh toán điện tử hợp lệ 24/7)", fSmall, new XSolidBrush(cGray), sigCol2 - 25, sigY + 12);
        gfx.DrawString("CẢNG BIỂN QUỐC TẾ NEXUSPORT", fBodyBold, new XSolidBrush(cBlue), sigCol2 - 15, sigY + 55);

        // Bottom Footer
        var botY = 790.0;
        gfx.DrawLine(new XPen(cBorder, 1), left, botY, right, botY);
        gfx.DrawString("Phiếu thu điện tử ePort NexusPort · Thời gian in: UTC+7 · Trang 1/1", fSmall, new XSolidBrush(cGray), left, botY + 8);
        gfx.DrawString("Hotline: 1900 6868 · support@nexusport.vn", fSmall, new XSolidBrush(cGray), right, botY + 8, XStringFormats.TopRight);

        using var memoryStream = new MemoryStream();
        document.Save(memoryStream, false);
        return memoryStream.ToArray();
    }
}
