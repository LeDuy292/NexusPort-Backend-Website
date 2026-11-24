'use strict';

const nodemailer = require('nodemailer');

function getTransporter() {
  const host = process.env.SMTP_HOST || process.env.EmailSettings__SmtpHost || 'smtp.gmail.com';
  const port = parseInt(process.env.SMTP_PORT || process.env.EmailSettings__SmtpPort || '587', 10);
  const user = process.env.SMTP_USER || process.env.EmailSettings__SmtpUser || 'smartlogai2026@gmail.com';
  const pass = (process.env.SMTP_PASS || process.env.EmailSettings__SmtpPass || 'wloa fwpj estp dkga').replace(/\s+/g, '');

  return nodemailer.createTransport({
    host,
    port,
    secure: port === 465,
    auth: { user, pass },
  });
}

/**
 * Gửi email chứa mã OTP khôi phục mật khẩu
 * @param {string} toEmail 
 * @param {string} otpCode 
 * @param {string} fullNameOrUsername 
 */
async function sendPasswordResetOtpEmail(toEmail, otpCode, fullNameOrUsername) {
  const transporter = getTransporter();
  const senderEmail = process.env.EmailSettings__SenderEmail || process.env.SMTP_USER || 'smartlogai2026@gmail.com';
  const senderName = process.env.EmailSettings__SenderName || 'NexusPort - Hệ Thống Cảng Container';

  const htmlContent = `
  <!DOCTYPE html>
  <html>
  <head>
    <meta charset="utf-8">
    <title>Mã OTP Khôi Phục Mật Khẩu - NexusPort</title>
  </head>
  <body style="font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; background-color: #0b0f19; margin: 0; padding: 30px; color: #f1f5f9;">
    <table align="center" border="0" cellpadding="0" cellspacing="0" width="100%" style="max-width: 540px; background-color: #121826; border: 1px solid #1e293b; border-radius: 16px; overflow: hidden; box-shadow: 0 10px 25px rgba(0,0,0,0.5);">
      <tr>
        <td style="padding: 28px 32px; background: linear-gradient(135deg, #1e293b 0%, #0f172a 100%); border-bottom: 2px solid #ff682c;">
          <table border="0" cellpadding="0" cellspacing="0" width="100%">
            <tr>
              <td>
                <span style="font-size: 24px; font-weight: 800; color: #ffffff; letter-spacing: -0.5px;">NexusPort<span style="color: #ff682c;">.</span></span>
                <span style="display: block; font-size: 11px; text-transform: uppercase; letter-spacing: 1.5px; color: #94a3b8; margin-top: 4px;">Terminal Management System</span>
              </td>
            </tr>
          </table>
        </td>
      </tr>

      <tr>
        <td style="padding: 36px 32px 28px 32px;">
          <h2 style="margin: 0 0 12px 0; font-size: 20px; font-weight: 700; color: #ffffff;">Yêu cầu đặt lại mật khẩu</h2>
          <p style="margin: 0 0 20px 0; font-size: 14px; line-height: 1.6; color: #94a3b8;">
            Xin chào <strong style="color: #f8fafc;">${fullNameOrUsername || 'quý khách'}</strong>, chúng tôi nhận được yêu cầu khôi phục mật khẩu cho tài khoản NexusPort liên kết với email này.
          </p>

          <p style="margin: 0 0 10px 0; font-size: 13px; font-weight: 600; text-transform: uppercase; letter-spacing: 0.5px; color: #cbd5e1;">Mã xác thực OTP của bạn:</p>
          
          <div style="background-color: #080d1a; border: 2px dashed #ff682c; border-radius: 12px; padding: 18px 24px; text-align: center; margin-bottom: 24px;">
            <span style="font-family: 'Courier New', monospace; font-size: 34px; font-weight: 800; letter-spacing: 8px; color: #ff682c; display: inline-block;">
              ${otpCode}
            </span>
          </div>

          <p style="margin: 0 0 8px 0; font-size: 13px; color: #94a3b8;">
            ⏱️ Mã OTP có hiệu lực trong vòng <strong>15 phút</strong>.
          </p>
          <p style="margin: 0 0 24px 0; font-size: 13px; color: #e2e8f0; background: rgba(239, 68, 68, 0.1); border-left: 3px solid #ef4444; padding: 10px 14px; border-radius: 4px;">
            ⚠️ <strong>Cảnh báo bảo mật:</strong> Tuyệt đối không chia sẻ mã này cho bất kỳ ai. Nếu bạn không gửi yêu cầu này, hãy bảo mật tài khoản ngay lập tức.
          </p>
        </td>
      </tr>

      <tr>
        <td style="padding: 20px 32px; background-color: #080d1a; border-top: 1px solid #1e293b; text-align: center;">
          <p style="margin: 0; font-size: 12px; color: #64748b;">
            © 2026 NexusPort. Cảng Tiên Sa, Đà Nẵng, Việt Nam.<br/>
            Email tự động từ hệ thống, vui lòng không trả lời thư này.
          </p>
        </td>
      </tr>
    </table>
  </body>
  </html>
  `;

  return transporter.sendMail({
    from: `"${senderName}" <${senderEmail}>`,
    to: toEmail,
    subject: `[NexusPort] ${otpCode} là mã xác thực khôi phục mật khẩu của bạn`,
    text: `Mã OTP khôi phục mật khẩu NexusPort của bạn là: ${otpCode}. Mã có hiệu lực trong 15 phút. Tuyệt đối không chia sẻ mã này cho người khác.`,
    html: htmlContent,
  });
}

const escapeHtml = (v) => String(v == null ? '' : v).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

/**
 * Gửi thông tin đăng nhập cho tài khoản mới do quản trị viên tạo.
 * @param {string} toEmail  Email người nhận (ví dụ Gmail của hãng tàu)
 * @param {{ fullName?: string, username: string, password: string, roleLabel: string, organization?: string, loginUrl?: string }} info
 */
async function sendAccountCredentialsEmail(toEmail, info) {
  const transporter = getTransporter();
  const senderEmail = process.env.EmailSettings__SenderEmail || process.env.SMTP_USER || 'smartlogai2026@gmail.com';
  const senderName = process.env.EmailSettings__SenderName || 'NexusPort - Hệ Thống Cảng Container';
  const loginUrl = info.loginUrl || process.env.APP_URL || process.env.FRONTEND_URL || 'http://localhost:5173/login';
  const name = escapeHtml(info.fullName || info.username);
  const html = `<!DOCTYPE html><html><head><meta charset="utf-8"><title>Tài khoản NexusPort</title></head>
<body style="margin:0;padding:24px;background:#f1f5f9;font-family:Inter,-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Arial,sans-serif;color:#0f172a;">
  <table align="center" width="100%" style="max-width:520px;background:#ffffff;border:1px solid #e2e8f0;border-radius:16px;overflow:hidden;" cellpadding="0" cellspacing="0">
    <tr><td style="padding:24px 28px;border-bottom:3px solid #ff682c;"><span style="font-size:22px;font-weight:800;">NexusPort<span style="color:#ff682c;">.</span></span></td></tr>
    <tr><td style="padding:28px;">
      <h2 style="margin:0 0 12px;font-size:18px;">Tài khoản của bạn đã được tạo</h2>
      <p style="margin:0 0 18px;font-size:14px;line-height:1.6;color:#475569;">Xin chào <strong>${name}</strong>, quản trị viên đã tạo tài khoản <strong>${escapeHtml(info.roleLabel)}</strong>${info.organization ? ` cho <strong>${escapeHtml(info.organization)}</strong>` : ''} trên hệ thống NexusPort.</p>
      <table width="100%" cellpadding="0" cellspacing="0" style="background:#f8fafc;border:1px solid #e2e8f0;border-radius:12px;margin-bottom:18px;">
        <tr><td style="padding:14px 18px;font-size:13px;color:#64748b;">Tên đăng nhập</td><td style="padding:14px 18px;font-size:14px;font-weight:700;text-align:right;">${escapeHtml(info.username)}</td></tr>
        <tr><td style="padding:14px 18px;font-size:13px;color:#64748b;border-top:1px solid #e2e8f0;">Email</td><td style="padding:14px 18px;font-size:14px;font-weight:700;text-align:right;border-top:1px solid #e2e8f0;">${escapeHtml(toEmail)}</td></tr>
        <tr><td style="padding:14px 18px;font-size:13px;color:#64748b;border-top:1px solid #e2e8f0;">Mật khẩu</td><td style="padding:14px 18px;font-size:15px;font-weight:800;text-align:right;border-top:1px solid #e2e8f0;letter-spacing:.5px;">${escapeHtml(info.password)}</td></tr>
      </table>
      <p style="margin:0 0 20px;"><a href="${escapeHtml(loginUrl)}" style="display:inline-block;background:#ff682c;color:#ffffff;text-decoration:none;font-weight:700;font-size:14px;padding:12px 22px;border-radius:10px;">Đăng nhập NexusPort</a></p>
      <p style="margin:0;font-size:12px;line-height:1.6;color:#64748b;background:#fff7ed;border-left:3px solid #f59e0b;padding:10px 12px;">Vì lý do bảo mật, hãy đổi mật khẩu sau lần đăng nhập đầu tiên và không chia sẻ email này cho người khác.</p>
    </td></tr>
    <tr><td style="padding:16px 28px;background:#f8fafc;border-top:1px solid #e2e8f0;font-size:12px;color:#94a3b8;text-align:center;">Email tự động từ hệ thống NexusPort, vui lòng không trả lời thư này.</td></tr>
  </table>
</body></html>`;
  return transporter.sendMail({
    from: `"${senderName}" <${senderEmail}>`,
    to: toEmail,
    subject: '[NexusPort] Thông tin đăng nhập tài khoản của bạn',
    text: `Tài khoản ${info.roleLabel} NexusPort đã được tạo.\nTên đăng nhập: ${info.username}\nMật khẩu: ${info.password}\nĐăng nhập: ${loginUrl}\nHãy đổi mật khẩu sau lần đăng nhập đầu tiên.`,
    html,
  });
}

module.exports = {
  sendAccountCredentialsEmail,
  getTransporter,
  sendPasswordResetOtpEmail,
};

