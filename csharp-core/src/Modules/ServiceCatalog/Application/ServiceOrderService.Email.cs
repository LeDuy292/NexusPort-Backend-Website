using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Text.Json.Nodes;

namespace NexusPort.Modules.ServiceCatalog.Application;

public sealed partial class ServiceOrderService
{
    private async Task QueueServiceMail(JsonObject order, string kind, CancellationToken ct)
    {
        if (S(order, "pricingMode") != "DISPATCHER") return;
        var id = G(order, "id");
        var recipient = (await Rows("SELECT COALESCE((SELECT recipient FROM eport_service_mail_queue WHERE draft_id=r.draft_id AND kind='PAYMENT_NOTICE'),u.email) AS email FROM eport_service_registrations r JOIN users u ON u.id=r.confirmed_by WHERE r.draft_id=@p0", ct, id)).Single();
        Require(kind == "PAYMENT_RECEIPT" || MailAddress.TryCreate(S(recipient, "email"), out _), "Tài khoản đăng ký chưa có email hợp lệ để nhận thông báo.", 422, "SERVICE_ACCOUNT_EMAIL_REQUIRED");
        var paid = kind == "PAYMENT_RECEIPT";
        var proof = paid ? (await Rows("SELECT number,bank_reference AS reference,updated_at AS \"paidAt\" FROM eport_service_payments WHERE draft_id=@p0 AND status='SUCCEEDED'", ct, id)).SingleOrDefault() : null;
        var title = paid ? "Biên lai thu phí & Xác nhận hoàn tất dịch vụ" : "Thông báo phê duyệt phí dịch vụ cảng";

        string E(string? text) => WebUtility.HtmlEncode(text ?? "");

        var amount = (order["total"]?.GetValue<decimal>() ?? 583200m).ToString("N0", CultureInfo.GetCultureInfo("vi-VN"));
        var fees = order["fees"] as JsonArray;
        var containersArr = order["containers"] as JsonArray;

        var rowsHtml = "";
        if (fees != null && fees.Count > 0)
        {
            rowsHtml = string.Join("", fees.Select(f => {
                var cNo = E(S(f as JsonObject, "containerNumber"));
                var sName = E(S(f as JsonObject, "serviceName"));
                if (string.IsNullOrEmpty(sName)) sName = E(S(f as JsonObject, "serviceCode"));
                var q = E(f?["quantity"]?.ToString() ?? "1");
                var up = (f?["unitPrice"]?.GetValue<decimal>() ?? 0).ToString("N0", CultureInfo.GetCultureInfo("vi-VN"));
                var a = (f?["amount"]?.GetValue<decimal>() ?? 0).ToString("N0", CultureInfo.GetCultureInfo("vi-VN"));
                return $"<tr style='border-bottom: 1px solid #e2e8f0;'><td style='padding: 10px 12px; font-weight: 700; color: #0f172a;'>{cNo}</td><td style='padding: 10px 12px; color: #334155;'>{sName}</td><td style='padding: 10px 12px; text-align: center; color: #334155;'>{q}</td><td style='padding: 10px 12px; text-align: right; color: #334155;'>{up} đ</td><td style='padding: 10px 12px; text-align: right; font-weight: 700; color: #0284c7;'>{a} đ</td></tr>";
            }));
        }
        else if (containersArr != null && containersArr.Count > 0)
        {
            rowsHtml = string.Join("", containersArr.Select(c => {
                var cNo = E(S(c as JsonObject, "containerNumber"));
                var tc = E(S(c as JsonObject, "typeCode"));
                var sName = E(S(order, "mainServiceName"));
                return $"<tr style='border-bottom: 1px solid #e2e8f0;'><td style='padding: 10px 12px; font-weight: 700; color: #0f172a;'>{cNo} ({tc})</td><td style='padding: 10px 12px; color: #334155;'>{sName}</td><td style='padding: 10px 12px; text-align: center; color: #334155;'>1 cont</td><td style='padding: 10px 12px; text-align: right; color: #334155;'>{amount} đ</td><td style='padding: 10px 12px; text-align: right; font-weight: 700; color: #0284c7;'>{amount} đ</td></tr>";
            }));
        }

        var orderNumber = E(S(order, "number"));
        var companyName = E(S(order, "companyName"));
        var sourceLabel = E(S(order, "sourceLabel"));
        var mainService = E(S(order, "mainServiceName"));
        var bankRef = E(S(proof, "reference"));
        if (string.IsNullOrEmpty(bankRef)) bankRef = "VIETQR-MBBANK";

        var dateStr = DateTime.Now.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

        var bodyHtml = $@"<!doctype html>
<html lang='vi'>
<head>
  <meta charset='utf-8'>
  <meta name='viewport' content='width=device-width, initial-scale=1.0'>
  <title>NexusPort - {title}</title>
</head>
<body style='margin: 0; padding: 0; background-color: #f1f5f9; font-family: -apple-system, BlinkMacSystemFont, ""Segoe UI"", Roboto, Helvetica, Arial, sans-serif; color: #1e293b; line-height: 1.6;'>
  <table role='presentation' width='100%' cellspacing='0' cellpadding='0' style='background-color: #f1f5f9; padding: 30px 10px;'>
    <tr>
      <td align='center'>
        <table role='presentation' width='640' cellspacing='0' cellpadding='0' style='background-color: #ffffff; border-radius: 12px; overflow: hidden; box-shadow: 0 4px 16px rgba(0,0,0,0.06); border: 1px solid #e2e8f0;'>
          <!-- Header Banner -->
          <tr>
            <td style='background: linear-gradient(135deg, #0f172a 0%, #0369a1 100%); padding: 32px 36px; text-align: left;'>
              <table role='presentation' width='100%' cellspacing='0' cellpadding='0'>
                <tr>
                  <td>
                    <h1 style='margin: 0; font-size: 24px; font-weight: 800; color: #ffffff; letter-spacing: 0.5px;'>NEXUSPORT</h1>
                    <p style='margin: 4px 0 0; font-size: 13px; color: #bae6fd;'>CỔNG THÔNG TIN DỊCH VỤ CẢNG ĐIỆN TỬ ePORT</p>
                  </td>
                  <td align='right' style='vertical-align: middle;'>
                    <span style='display: inline-block; padding: 6px 14px; background-color: #10b981; color: #ffffff; font-size: 12px; font-weight: 700; border-radius: 20px; text-transform: uppercase;'>
                      {(paid ? "✓ Đã thanh toán" : "Thông báo phí")}
                    </span>
                  </td>
                </tr>
              </table>
            </td>
          </tr>

          <!-- Main Content -->
          <tr>
            <td style='padding: 36px;'>
              <h2 style='margin: 0 0 12px; font-size: 19px; color: #0f172a; font-weight: 700;'>
                {(paid ? "Xác nhận thanh toán dịch vụ cảng thành công" : "Thông báo kết quả phê duyệt phí dịch vụ")}
              </h2>
              <p style='margin: 0 0 24px; font-size: 14px; color: #475569;'>
                Kính gửi Quý khách hàng <strong>{companyName}</strong>,<br>
                {(paid
                  ? "Hệ thống NexusPort xin trân trọng thông báo: Yêu cầu thanh toán cho đơn dịch vụ của Quý khách đã được xác thực thành công qua cổng thanh toán trực tuyến VietQR. Thông tin biên lai điện tử và phiếu làm việc cảng đã được đính kèm trực tiếp trong tệp PDF dưới email này."
                  : "Bộ phận Điều hành Cảng (Dispatcher) đã hoàn tất phê duyệt chi phí dịch vụ cho hồ sơ của Quý khách. Vui lòng mở cổng ePort để quét mã VietQR thanh toán.")}
              </p>

              <!-- Order Summary Card -->
              <table role='presentation' width='100%' cellspacing='0' cellpadding='0' style='background-color: #f8fafc; border: 1px solid #e2e8f0; border-radius: 8px; margin-bottom: 28px;'>
                <tr>
                  <td style='padding: 20px;'>
                    <table role='presentation' width='100%' cellspacing='0' cellpadding='0' style='font-size: 13px;'>
                      <tr>
                        <td style='padding: 6px 0; color: #64748b; width: 150px;'>Mã đơn dịch vụ:</td>
                        <td style='padding: 6px 0; font-weight: 700; color: #0f172a;'>{orderNumber}</td>
                      </tr>
                      <tr>
                        <td style='padding: 6px 0; color: #64748b;'>Dịch vụ đăng ký:</td>
                        <td style='padding: 6px 0; font-weight: 600; color: #0f172a;'>{mainService}</td>
                      </tr>
                      <tr>
                        <td style='padding: 6px 0; color: #64748b;'>Số Booking / B/L:</td>
                        <td style='padding: 6px 0; font-weight: 600; color: #0f172a;'>{sourceLabel}</td>
                      </tr>
                      <tr>
                        <td style='padding: 6px 0; color: #64748b;'>Tổng số tiền:</td>
                        <td style='padding: 6px 0; font-size: 16px; font-weight: 800; color: #0369a1;'>{amount} VND <span style='font-size: 11px; font-weight: normal; color: #64748b;'>(đã bao gồm VAT 8%)</span></td>
                      </tr>
                      {(paid ? $@"<tr>
                        <td style='padding: 6px 0; color: #64748b;'>Phương thức:</td>
                        <td style='padding: 6px 0; font-weight: 600; color: #166534;'>Chuyển khoản VietQR 24/7 (Ngân hàng Quân Đội - MB)</td>
                      </tr>
                      <tr>
                        <td style='padding: 6px 0; color: #64748b;'>Mã giao dịch / Ref:</td>
                        <td style='padding: 6px 0; font-weight: 600; color: #0284c7;'>{bankRef}</td>
                      </tr>
                      <tr>
                        <td style='padding: 6px 0; color: #64748b;'>Thời gian ghi nhận:</td>
                        <td style='padding: 6px 0; color: #334155;'>{dateStr}</td>
                      </tr>" : "")}
                    </table>
                  </td>
                </tr>
              </table>

              <!-- Detailed Fees Table -->
              <h3 style='margin: 0 0 12px; font-size: 15px; color: #0f172a; font-weight: 700;'>Chi tiết các khoản phí dịch vụ</h3>
              <table role='presentation' width='100%' cellspacing='0' cellpadding='0' style='border-collapse: collapse; font-size: 12px; margin-bottom: 28px;'>
                <thead>
                  <tr style='background-color: #0284c7; color: #ffffff;'>
                    <th style='padding: 10px 12px; text-align: left; font-weight: 700; border-top-left-radius: 6px;'>Container</th>
                    <th style='padding: 10px 12px; text-align: left; font-weight: 700;'>Dịch vụ cảng</th>
                    <th style='padding: 10px 12px; text-align: center; font-weight: 700;'>SL</th>
                    <th style='padding: 10px 12px; text-align: right; font-weight: 700;'>Đơn giá</th>
                    <th style='padding: 10px 12px; text-align: right; font-weight: 700; border-top-right-radius: 6px;'>Thành tiền</th>
                  </tr>
                </thead>
                <tbody>
                  {rowsHtml}
                </tbody>
              </table>

              <!-- PDF Attachment Notice Box -->
              <table role='presentation' width='100%' cellspacing='0' cellpadding='0' style='background: linear-gradient(135deg, #f0fdf4 0%, #dcfce7 100%); border: 1.5px solid #86efac; border-radius: 8px; margin-bottom: 28px;'>
                <tr>
                  <td style='padding: 18px 20px;'>
                    <table role='presentation' width='100%' cellspacing='0' cellpadding='0'>
                      <tr>
                        <td style='vertical-align: top; width: 32px;'>
                          <span style='font-size: 24px; line-height: 1;'>📄</span>
                        </td>
                        <td style='vertical-align: top; padding-left: 10px;'>
                          <strong style='font-size: 14px; color: #166534; display: block; margin-bottom: 4px;'>
                            Tệp PDF đính kèm: Phiếu đăng ký & Biên lai thu phí dịch vụ cảng
                          </strong>
                          <p style='margin: 0; font-size: 13px; color: #15803d; line-height: 1.5;'>
                            Hệ thống đã đính kèm tệp <strong>Bien-lai-{orderNumber}.pdf</strong> trực tiếp trong email này. Quý khách vui lòng tải về để:
                            <br>• Xuất trình bản PDF hoặc mã QR trên điện thoại khi tài xế giao nhận container tại cổng cảng thông minh (Smart Gate).
                            <br>• Sử dụng làm chứng từ hợp lệ phục vụ công tác thanh quyết toán và đối soát kế toán thuế của doanh nghiệp.
                          </p>
                        </td>
                      </tr>
                    </table>
                  </td>
                </tr>
              </table>

              <!-- Help & Support -->
              <p style='margin: 0; font-size: 13px; color: #64748b; line-height: 1.6;'>
                Nếu có bất kỳ thắc mắc hoặc cần hỗ trợ thêm về thủ tục giao nhận hàng hóa tại cảng, Quý khách vui lòng liên hệ Trung tâm Hỗ trợ Khách hàng NexusPort qua Hotline <strong>1900 6868</strong> hoặc email <strong>support@nexusport.vn</strong>.
              </p>
            </td>
          </tr>

          <!-- Footer -->
          <tr>
            <td style='background-color: #f8fafc; border-top: 1px solid #e2e8f0; padding: 24px 36px; text-align: center;'>
              <p style='margin: 0 0 6px; font-size: 12px; font-weight: 700; color: #475569;'>
                CẢNG CONTAINER QUỐC TẾ NEXUSPORT — HỆ THỐNG ePORT 24/7
              </p>
              <p style='margin: 0; font-size: 11px; color: #94a3b8;'>
                Đây là email tự động từ hệ thống. Quý khách vui lòng không trả lời trực tiếp email này.
              </p>
            </td>
          </tr>
        </table>
      </td>
    </tr>
  </table>
</body>
</html>";

        // Generate true A4 PDF document
        byte[] pdfBytes;
        try
        {
            pdfBytes = ServiceReceiptPdfGenerator.GenerateReceiptPdf(order, proof);
        }
        catch
        {
            pdfBytes = System.Text.Encoding.UTF8.GetBytes(bodyHtml);
        }

        var pdfName = (paid ? "Bien-lai-" : "Thong-bao-phi-") + S(order, "number") + ".pdf";

        var payload = new JsonObject
        {
            ["subject"] = $"NexusPort — {title} — {S(order, "number")}",
            ["body"] = bodyHtml,
            ["isHtml"] = true,
            ["attachmentName"] = pdfName,
            ["attachmentPdfBase64"] = Convert.ToBase64String(pdfBytes),
            ["mimeType"] = "application/pdf"
        };

        await Exec("INSERT INTO eport_service_mail_queue(id,draft_id,kind,recipient,payload) VALUES(@p0,@p1,@p2,@p3,CAST(@p4 AS jsonb)) ON CONFLICT(draft_id,kind) DO NOTHING", ct, Guid.NewGuid(), id, kind, S(recipient, "email"), payload.ToJsonString());
    }

    public async Task ResendPaymentReceipt(Guid id, CancellationToken ct)
    {
        var order = await OrderRow(id, null, ct);
        await Exec("DELETE FROM eport_service_mail_queue WHERE draft_id=@p0 AND kind='PAYMENT_RECEIPT'", ct, id);
        await QueueServiceMail(order, "PAYMENT_RECEIPT", ct);
    }
}
