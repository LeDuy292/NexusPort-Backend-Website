# Dữ liệu mẫu hàng nhập

Hai bộ riêng cho `dauboquay` và `carrier01`, chỉ dùng Development. Hạn lượt mẫu và biểu phí: **11/12/2026**.

| Loại | dauboquay | carrier01 | Phí DEMO/container |
| --- | --- | --- | --- |
| 20GP | DEMU6111017 | DEMU6112013 | 300.000 VND |
| 20GP | DEMU6111022 | DEMU6112029 | 300.000 VND |
| 40GP | DEMU6111038 | DEMU6112034 | 500.000 VND |
| 20RF | DEMU6111043 | DEMU6112040 | 300.000 VND + điện lạnh |

## Chạy thử

1. Đăng nhập đúng tài khoản, chọn **Giao hàng nhập**.
2. Dùng DO `DEMO-DO-IMPORT-20261111`, Bill `DEMO-BL-IMPORT-20261111`; với `carrier01` thêm `-CARRIER01` vào hai mã.
3. Tàu `DEMO IMPORT VESSEL`, chuyến `DEMO-I-001`; chọn hạn lệnh còn hiệu lực và xác thực email.
4. Chọn container, dịch vụ kèm; 20RF cần cắm điện. Bấm **Xác nhận thông quan**.
5. Khai báo giao nhận, giờ dự kiến trong tương lai; tích xác nhận thông tin và gửi đăng ký cho Dispatcher.
6. Chờ phần Dispatcher trả kết quả và số tiền; sau đó thanh toán PayOS, nhận thông báo/biên lai qua email.

## Nạp mẫu

Chạy tại backend:

```powershell
.\database\scripts\seed-eport-import-demo.ps1 -Username carrier01
.\database\scripts\seed-eport-import-demo.ps1 -Username carrier01 -Apply
```

Mặc định chỉ kiểm tra; `-Apply` ghi mẫu vào DB Development trong `.env`. Đổi Username thành `dauboquay` để chọn bộ kia. Script không sửa/xóa dữ liệu cũ; chạy lại không nhân bản. Mẫu không phải chứng từ, biểu phí hay kết quả thông quan thật.
