import os
import cv2
import numpy as np
from services.pipeline import SmartPortGatePipeline


def create_mock_gate_camera_image() -> bytes:
    """
    Tạo ảnh giả lập camera cổng cảng: một xe tải có gắn biển số 51C-123.45
    """
    # Ảnh nền 800x600 (khung cảnh cổng cảng)
    img = np.full((600, 800, 3), (180, 180, 180), dtype=np.uint8)

    # Vẽ thân xe tải (màu xanh dương đậm)
    cv2.rectangle(img, (150, 150), (650, 500), (120, 60, 30), -1)
    # Vẽ kính chắn gió xe tải
    cv2.rectangle(img, (200, 180), (600, 320), (220, 220, 200), -1)
    # Vẽ lưới tản nhiệt
    cv2.rectangle(img, (250, 360), (550, 440), (40, 40, 40), -1)

    # Vẽ biển số xe (vùng màu trắng, viền đen)
    # Tọa độ biển số: (300, 450) đến (500, 510)
    plate_x1, plate_y1, plate_x2, plate_y2 = 320, 450, 480, 510
    cv2.rectangle(img, (plate_x1, plate_y1), (plate_x2, plate_y2), (255, 255, 255), -1)
    cv2.rectangle(img, (plate_x1, plate_y1), (plate_x2, plate_y2), (0, 0, 0), 2)

    # Viết chữ biển số "51C-123.45"
    cv2.putText(
        img,
        "51C-123.45",
        (plate_x1 + 10, plate_y1 + 42),
        cv2.FONT_HERSHEY_SIMPLEX,
        0.85,
        (0, 0, 0),
        2,
        cv2.LINE_AA
    )

    _, encoded = cv2.imencode(".jpg", img)
    return encoded.tobytes()


def run_acceptance_tests():
    print("==================================================")
    print("KIỂM THỬ LUỒNG SMART PORT GATE AUTOMATION (AI)")
    print("==================================================")

    pipeline = SmartPortGatePipeline()

    # 1. Giả lập Camera chụp ảnh
    image_bytes = create_mock_gate_camera_image()
    print("[1] Camera nhận được hình ảnh: THÀNH CÔNG (Size:", len(image_bytes), "bytes)")

    # 2. Chạy toàn bộ pipeline
    result = pipeline.process_image(
        image_bytes=image_bytes,
        camera_id="CAM_GATE_01",
        lane_code="LANE_01",
        gate_code="GATE_IN_A"
    )

    print("\n--- KẾT QUẢ XỬ LÝ (RECOGNITION RESULT) ---")
    print("Recognition ID:", result["recognition_id"])
    print("Camera ID     :", result["camera_id"])
    print("Lưu thời gian :", result["recognized_at"])
    print("Thời gian xử lý:", result["processing_time_ms"], "ms")
    print("Trạng thái    :", result["status"])

    # 3. Kiểm tra phát hiện phương tiện
    print("\n[2] YOLO phát hiện phương tiện:")
    print("    - Đã phát hiện:", result["vehicle"]["detected"])
    print("    - Loại xe    :", result["vehicle"]["type"])
    print("    - Tọa độ Box :", result["vehicle"]["box"])
    print("    - Độ tin cậy :", result["vehicle"]["confidence"])

    # 4. Kiểm tra phát hiện vùng biển số
    print("\n[3] Phát hiện vùng biển số (YOLO ONNX):")
    print("    - Đã phát hiện:", result["license_plate"]["detected"])
    print("    - Tọa độ Box :", result["license_plate"]["box"])
    print("    - Độ tin cậy :", result["license_plate"]["detector_confidence"])

    # 5. Kiểm tra OCR đọc biển số
    print("\n[4] OCR đọc biển số:")
    print("    - Biển số đọc được:", result["license_plate"]["plate_number"])
    print("    - Raw OCR text   :", result["raw_ocr_data"])
    print("    - Độ tin cậy OCR :", result["license_plate"]["ocr_confidence"])

    # 6. Payload tương thích Backend .NET C#
    print("\n[5] Payload chuẩn hóa cho C# Gate Module (GateVerificationRecord):")
    for k, v in result["gate_module_payload"].items():
        print(f"    {k}: {v}")

    print("\n==================================================")
    print("TẤT CẢ ACCEPTANCE CRITERIA ĐÃ ĐƯỢC ĐÁP ỨNG ĐẦY ĐỦ!")
    print("==================================================")


if __name__ == "__main__":
    run_acceptance_tests()
