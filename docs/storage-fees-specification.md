# 7. ĐẶC TẢ NGHIỆP VỤ & TÍNH NĂNG: PHÍ LƯU BÃI (STORAGE FEES — CÁN BỘ CHỌN CHO KHÁCH HÀNG)

## 1. Mục đích nghiệp vụ
Trong hệ thống quản lý cảng biển / depot / bãi container (NEXUSPORT):
- **Phí lưu bãi (Storage Fee)** là khoản phụ phí phát sinh khi container hoặc hàng hóa nằm tại bãi vượt quá thời hạn lưu bãi miễn phí quy định (**Free Time**, thường từ 3 – 5 ngày).
- **Cơ chế cán bộ chọn cho khách hàng:** Khách hàng/tài xế không tự kê khai mức phí này. Khi container bị để quá ngày (Overdue), **Cán bộ quản lý / Thu ngân cảng (Billing Staff)** sẽ kiểm tra số ngày vượt quá hạn và **nhập/chọn một đơn giá phát sinh mỗi ngày** để tính thêm tiền phát sinh cho khách hàng.

---

## 2. Quy trình & Công thức tính phí phát sinh theo ngày

1. **Số ngày lưu thực tế:**
   $$\text{Số ngày lưu bãi} = \text{Ngày Gate-Out (lấy ra)} - \text{Ngày Gate-In (hạ bãi)}$$

2. **Số ngày quá hạn:**
   $$\text{Số ngày quá hạn} = \max(0, \text{Số ngày lưu bãi} - \text{Số ngày Free Time})$$

3. **Công thức tính tiền phát sinh:**
   $$\text{Tổng tiền phí lưu bãi phát sinh} = \text{Số ngày quá hạn} \times \text{Đơn giá phát sinh mỗi ngày (VND/ngày)}$$

   * *Ví dụ:*
     * Container 40ft để quá hạn **4 ngày**.
     * Cán bộ áp đơn giá phát sinh: **80.000 VNĐ / ngày**.
     * **Tổng tiền thu thêm:** $4 \times 80.000\text{đ} = \mathbf{320.000\text{ VNĐ}}$.

---

## 3. Các mức đơn giá phát sinh đề xuất (Cán bộ có thể tự nhập tùy chỉnh)
- **Container 20ft (Dry/GP):** Mặc định `40.000 VNĐ / ngày`.
- **Container 40ft (HC/HQ):** Mặc định `80.000 VNĐ / ngày`.
- **Mức phạt trễ / quá tải cao điểm:** `150.000 VNĐ / ngày`.
- **Container Lạnh (40ft Reefer):** `400.000 VNĐ / ngày` (đã gồm tiền điện & đo kiểm nhiệt độ).
- **Tùy biến:** Cán bộ có toàn quyền nhập bất kỳ mức đơn giá phát sinh nào theo từng hợp đồng hoặc quyết định miễn giảm tại quầy.

---

## 4. Giao diện & Thao tác trên hệ thống
- **Đường dẫn:** `/admin/billing` (Tab **`⚡ Phí lưu bãi (Mục 7)`**)
- **Menu Sidebar:** `Billing & Phí Lưu Bãi`
- Cán bộ nhập/chọn đơn giá phát sinh $\rightarrow$ Hệ thống tự động tính realtime $\rightarrow$ Bấm **"Lập hóa đơn cho khách"** hoặc **"Thu tiền tại quầy (Gate-Out)"**.
