"""
gate_runner.py — NexusPort Smart Gate Hardware Runner
======================================================
Script chạy độc lập (ngoài FastAPI) trên Laptop để:
  1. Lắng nghe tín hiệu "CAPTURE" từ ESP32 qua Serial (COM7 / 115200 baud)
  2. Chụp khung hình từ Webcam USB (index 0 = Cổng Vào, index 1 = Cổng Ra)
  3. Gọi SmartPortGatePipeline để OCR biển số (Azure AI Vision)
  4. Đối chiếu với NexusPort Backend (Node.js Express) để kiểm tra biển số hợp lệ
  5. Gửi "OPEN\\n" hoặc "FAIL\\n" xuống ESP32

Cách chạy:
  Đảm bảo đã đóng Serial Monitor trên Arduino IDE trước khi chạy.

  python gate_runner.py
  python gate_runner.py --port COM7 --cam-in 0 --cam-out 1 --no-verify
  python gate_runner.py --port COM7 --cam-in 0 --manual   # Test thủ công bằng phím 'c'

Các biến môi trường (tùy chọn, đọc từ ai-service/.env):
  GATE_SERIAL_PORT      Cổng COM (mặc định: COM7)
  GATE_BAUD_RATE        Baudrate  (mặc định: 115200)
  GATE_CAMERA_IN        Camera index cổng Vào (mặc định: 0)
  GATE_CAMERA_OUT       Camera index cổng Ra  (mặc định: 1)
  EXPRESS_BASE_URL      URL Backend Node.js để verify biển số
                        (mặc định: http://localhost:3001)
"""

import os
import sys
import time
import logging
import argparse
import threading
import base64
from typing import Optional

import cv2
import numpy as np
import requests
from dotenv import load_dotenv

# ── Load .env từ thư mục ai-service ──────────────────────────────────────────
_service_dir = os.path.dirname(os.path.abspath(__file__))
load_dotenv(os.path.join(_service_dir, ".env"), override=False)
load_dotenv(os.path.join(os.path.dirname(_service_dir), ".env"), override=False)

# ── Import nội bộ ─────────────────────────────────────────────────────────────
sys.path.insert(0, _service_dir)
from services.pipeline import SmartPortGatePipeline
from services.esp32_controller import ESP32Controller

# ── Logging ──────────────────────────────────────────────────────────────────
logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(name)s: %(message)s",
    handlers=[logging.StreamHandler(sys.stdout)],
)
logger = logging.getLogger("gate_runner")

# ── Cấu hình ─────────────────────────────────────────────────────────────────
SERIAL_PORT   = os.getenv("GATE_SERIAL_PORT",  "COM7")
BAUD_RATE     = int(os.getenv("GATE_BAUD_RATE", "115200"))
CAMERA_INDEX  = int(os.getenv("GATE_CAMERA_INDEX", os.getenv("GATE_CAMERA_IN", "0")))
CAMERA_IN     = CAMERA_INDEX
CAMERA_OUT    = CAMERA_INDEX
EXPRESS_URL   = os.getenv("EXPRESS_BASE_URL", "http://localhost:3001").rstrip("/")

# Gate / Lane mặc định cho làn Vào
GATE_CODE_IN  = os.getenv("GATE_CODE_IN",  "GATE_IN_A")
LANE_CODE_IN  = os.getenv("LANE_CODE_IN",  "LANE_01")
CAMERA_ID_IN  = os.getenv("CAMERA_ID_IN",  "CAM_GATE_IN_01")

# Gate / Lane mặc định cho làn Ra (dự phòng, dùng chung ESP32)
GATE_CODE_OUT = os.getenv("GATE_CODE_OUT", "GATE_OUT_A")
LANE_CODE_OUT = os.getenv("LANE_CODE_OUT", "LANE_02")
CAMERA_ID_OUT = os.getenv("CAMERA_ID_OUT", "CAM_GATE_OUT_01")


# ─────────────────────────────────────────────────────────────────────────────
# Hàm kiểm tra biển số với NexusPort Backend
# ─────────────────────────────────────────────────────────────────────────────

def verify_plate_with_backend(plate_number: str, gate_code: str) -> bool:
    """
    Gọi API NexusPort Node.js để kiểm tra biển số xe có hợp lệ không.
    Endpoint mẫu: GET /api/gate/verify-plate?plate=51C-123.45&gateCode=GATE_IN_A

    Returns:
        True  nếu biển số hợp lệ (xe được phép vào/ra).
        False nếu không hợp lệ hoặc backend không phản hồi.
    """
    try:
        url = f"{EXPRESS_URL}/api/gate/verify-plate"
        resp = requests.get(
            url,
            params={"plate": plate_number, "gateCode": gate_code},
            timeout=3,
        )
        if resp.status_code == 200:
            data = resp.json()
            is_valid = data.get("valid", False) or data.get("isValid", False)
            logger.info(
                "[verify] plate=%s gate=%s → valid=%s (backend response: %s)",
                plate_number, gate_code, is_valid, data,
            )
            return bool(is_valid)
        else:
            logger.warning(
                "[verify] Backend returned HTTP %d for plate=%s",
                resp.status_code, plate_number,
            )
            return False
    except requests.exceptions.ConnectionError:
        logger.warning(
            "[verify] Cannot reach backend at %s — treating plate as FAIL", EXPRESS_URL
        )
        return False
    except Exception as e:
        logger.error("[verify] Unexpected error: %s", e)
        return False


# ─────────────────────────────────────────────────────────────────────────────
# Lớp GateRunner chính
# ─────────────────────────────────────────────────────────────────────────────

class GateRunner:
    """
    Điều phối luồng hoạt động của cổng cảng thông minh:
      ESP32 IR Sensor → CAPTURE signal → Webcam grab → OCR → Verify → OPEN/FAIL
    """

    def __init__(
        self,
        port: str = SERIAL_PORT,
        baud: int = BAUD_RATE,
        cam_in: int = CAMERA_IN,
        cam_out: int = CAMERA_OUT,
        no_verify: bool = False,
        manual_mode: bool = False,
    ):
        self.port        = port
        self.baud        = baud
        self.cam_in_idx  = cam_in
        self.cam_out_idx = cam_out
        self.no_verify   = no_verify   # Bỏ qua verify backend (chỉ cần đọc được biển)
        self.manual_mode = manual_mode # Test thủ công bằng phím 'c'

        self._pipeline: Optional[SmartPortGatePipeline] = None
        self._esp32:    Optional[ESP32Controller]        = None
        self._cap_in:   Optional[cv2.VideoCapture]       = None
        self._cap_out:  Optional[cv2.VideoCapture]       = None

        # Lock để tránh xử lý đồng thời 2 tín hiệu CAPTURE
        self._processing_lock = threading.Lock()

    # ── Khởi tạo ─────────────────────────────────────────────────────────────

    def _open_camera(self, idx: int) -> Optional[cv2.VideoCapture]:
        """Thử mở camera với các backend DirectShow, MSMF hoặc Auto."""
        for backend in [cv2.CAP_DSHOW, cv2.CAP_MSMF, cv2.CAP_ANY]:
            try:
                cap = cv2.VideoCapture(idx, backend)
                if cap.isOpened():
                    return cap
                cap.release()
            except Exception:
                pass
        return None

    def setup(self):
        logger.info("=" * 60)
        logger.info("NexusPort Smart Gate Runner — Khởi động")
        logger.info("=" * 60)

        # 1. Khởi tạo AI Pipeline
        logger.info("[setup] Đang tải SmartPortGatePipeline (YOLO + OCR)...")
        self._pipeline = SmartPortGatePipeline()
        logger.info("[setup] Pipeline sẵn sàng.")

        # Camera chỉ được mở tạm thời khi có cảm biến hoặc lệnh quét.
        logger.info("[setup] Camera đang chờ tín hiệu cảm biến/lệnh quét.")

        # 2. Kết nối ESP32
        self._esp32 = ESP32Controller(port=self.port, baud=self.baud)
        logger.info("[setup] Đang kết nối ESP32 tại %s ...", self.port)
        connected = self._esp32.start()
        if connected:
            logger.info("[setup] ESP32 đã kết nối thành công.")
        else:
            logger.warning(
                "[setup] Chưa kết nối được ESP32 (sẽ tự retry). "
                "Đảm bảo đóng Serial Monitor trên Arduino IDE!"
            )

    # ── Chụp ảnh từ webcam ───────────────────────────────────────────────────

    def _grab_frame(self, cap: cv2.VideoCapture, label: str) -> Optional[bytes]:
        """Chụp 1 khung hình từ VideoCapture, trả về bytes JPEG hoặc None."""
        if cap is None or not cap.isOpened():
            logger.error("[grab] Camera %s không khả dụng!", label)
            return None

        # Đọc vài frame để bỏ buffer cũ (tránh ảnh bị delay)
        for _ in range(3):
            cap.grab()

        ret, frame = cap.read()
        if not ret or frame is None:
            logger.error("[grab] Không đọc được khung hình từ camera %s!", label)
            return None

        success, buf = cv2.imencode(
            ".jpg", frame, [int(cv2.IMWRITE_JPEG_QUALITY), 95]
        )
        if not success:
            return None

        logger.debug("[grab] Frame %s captured (%d bytes)", label, len(buf.tobytes()))
        return buf.tobytes()

    # ── Xử lý OCR + quyết định OPEN/FAIL ────────────────────────────────────

    def _process_capture(self, lane: str = "IN"):
        """
        Chụp ảnh, chạy OCR, verify biển số, gửi lệnh xuống ESP32.
        Chạy trong background thread để không block reader loop của ESP32.
        """
        with self._processing_lock:
            logger.info("[process] ── Bắt đầu xử lý CAPTURE (làn: %s) ──", lane)

            # Chọn làn logic; một webcam vật lý được dùng lần lượt cho cả hai làn.
            if lane == "IN":
                camera_index = self.cam_in_idx
                gate_code  = GATE_CODE_IN
                lane_code  = LANE_CODE_IN
                camera_id  = CAMERA_ID_IN
                cam_label  = "Cổng Vào"
            else:
                camera_index = self.cam_out_idx
                gate_code  = GATE_CODE_OUT
                lane_code  = LANE_CODE_OUT
                camera_id  = CAMERA_ID_OUT
                cam_label  = "Cổng Ra"

            # 1. Chụp ảnh từ Webcam
            cap = self._open_camera(camera_index)
            if cap is None or not cap.isOpened():
                logger.error("[process] Không mở được webcam index %d — gửi FAIL", camera_index)
                self._esp32.send_fail()
                return
            image_bytes = self._grab_frame(cap, cam_label)
            cap.release()
            if not image_bytes:
                logger.error("[process] Không lấy được ảnh — gửi FAIL")
                self._esp32.send_fail()
                return

            # 2. Chạy AI Pipeline (YOLO + Azure OCR)
            try:
                result = self._pipeline.process_image(
                    image_bytes=image_bytes,
                    camera_id=camera_id,
                    lane_code=lane_code,
                    gate_code=gate_code,
                )
            except Exception as e:
                logger.error("[process] Pipeline error: %s — gửi FAIL", e)
                self._esp32.send_fail()
                return

            plate_info   = result.get("license_plate", {})
            plate_number = plate_info.get("plate_number", "")
            ocr_conf     = plate_info.get("ocr_confidence", 0.0)

            logger.info(
                "[process] OCR kết quả: plate='%s' (conf=%.3f) | status=%s",
                plate_number, ocr_conf, result.get("status", "?"),
            )

            # 3. Kiểm tra biển số
            if not plate_number:
                logger.info("[process] Không đọc được biển số → FAIL")
                self._esp32.send_fail()
                return

            # 4. Đối chiếu với Backend (tùy chọn)
            if self.no_verify:
                is_valid = True
                logger.info("[process] --no-verify mode: bỏ qua kiểm tra backend.")
            else:
                is_valid = verify_plate_with_backend(plate_number, gate_code)

            # 5. Gửi lệnh xuống ESP32
            if is_valid:
                logger.info(
                    "[process] ✅ Biển số %s HỢP LỆ → OPEN (gate=%s, lane=%s)",
                    plate_number, gate_code, lane_code,
                )
                self._esp32.send_open()
            else:
                logger.info(
                    "[process] ❌ Biển số %s KHÔNG hợp lệ → FAIL",
                    plate_number,
                )
                self._esp32.send_fail()

            # 6. Broadcast lên Node.js (nếu có) để realtime UI
            self._broadcast(result, image_bytes)

    def _broadcast(self, result: dict, image_bytes: bytes):
        """Gửi kết quả lên Express /api/gate/iot-event (best-effort, không block)."""
        try:
            plate_obj = result.get("license_plate", {})
            plate     = plate_obj.get("plate_number")
            if not plate:
                return

            overview_b64 = base64.b64encode(image_bytes).decode("utf-8")
            payload = {
                "gateCode":           result.get("gate_code", GATE_CODE_IN),
                "laneCode":           result.get("lane_code", LANE_CODE_IN),
                "cameraId":           result.get("camera_id", CAMERA_ID_IN),
                "detectedPlate":      plate,
                "plateConfidence":    plate_obj.get("ocr_confidence", 0.0),
                "vehicleDetected":    True,
                "vehicleType":        result.get("vehicle", {}).get("type", "container_truck"),
                "vehiclePlateImageUrl": plate_obj.get("plate_image_base64"),
                "overviewImageUrl":   f"data:image/jpeg;base64,{overview_b64}",
                "timestamp":          result.get("recognized_at"),
                "source":             "ESP32_Hardware",
            }
            url = f"{EXPRESS_URL}/api/gate/iot-event"
            requests.post(url, json=payload, timeout=1.5)
            logger.debug("[broadcast] Đã gửi kết quả lên %s", url)
        except Exception as e:
            logger.warning("[broadcast] Không gửi được lên backend: %s", e)

    # ── Vòng lặp chính ───────────────────────────────────────────────────────

    def run(self):
        """Vòng lặp chính: chờ CAPTURE từ ESP32 hoặc phím 'c', xử lý OCR."""
        self.setup()

        logger.info("")
        logger.info("🚀 Gate Runner đang chạy. Nhấn 'q' để thoát.")
        if self.manual_mode:
            logger.info("   [Manual Mode] Nhấn 'c' để giả lập tín hiệu CAPTURE từ cảm biến IR.")
        logger.info("")

        # Không mở camera preview liên tục; camera chỉ bật trong lần quét.
        show_preview = False

        try:
            while True:
                # ── Hiển thị preview webcam ──
                if show_preview and self._cap_in and self._cap_in.isOpened():
                    ret, frame = self._cap_in.read()
                    if ret:
                        # Vẽ trạng thái lên preview
                        esp_status = "🟢 Connected" if self._esp32.connected else "🔴 Disconnected"
                        cv2.putText(
                            frame,
                            f"ESP32: {esp_status} | Port: {self.port}",
                            (10, 30), cv2.FONT_HERSHEY_SIMPLEX, 0.7, (0, 255, 0), 2,
                        )
                        cv2.putText(
                            frame,
                            "Nhan 'c' de test | 'q' de thoat",
                            (10, 60), cv2.FONT_HERSHEY_SIMPLEX, 0.6, (255, 255, 0), 2,
                        )
                        cv2.imshow("NexusPort Gate — Cong Vao [CAM 0]", frame)

                key = cv2.waitKey(30) & 0xFF

                # ── Thoát ──
                if key == ord('q'):
                    logger.info("Nhận phím 'q' — thoát Gate Runner.")
                    break

                # ── Test thủ công ──
                if key == ord('c'):
                    logger.info("[manual] Phím 'c' được nhấn — giả lập CAPTURE từ IR")
                    threading.Thread(
                        target=self._process_capture,
                        args=("IN",),
                        daemon=True,
                    ).start()
                    continue

                # ── Nhận CAPTURE từ ESP32 (non-blocking check) ──
                if self._esp32.capture_event.is_set():
                    self._esp32.capture_event.clear()
                    threading.Thread(
                        target=self._process_capture,
                        args=(self._esp32.capture_lane,),
                        daemon=True,
                    ).start()

        except KeyboardInterrupt:
            logger.info("Ctrl+C nhận được — dừng Gate Runner.")
        finally:
            self._cleanup()

    def _cleanup(self):
        logger.info("[cleanup] Đang giải phóng tài nguyên...")
        if self._esp32:
            self._esp32.stop()
        if self._cap_in:
            self._cap_in.release()
        if self._cap_out:
            self._cap_out.release()
        cv2.destroyAllWindows()
        logger.info("[cleanup] Hoàn tất. Chào tạm biệt!")


# ─────────────────────────────────────────────────────────────────────────────
# Entrypoint
# ─────────────────────────────────────────────────────────────────────────────

def parse_args():
    parser = argparse.ArgumentParser(
        description="NexusPort Smart Gate — ESP32 Hardware Runner"
    )
    parser.add_argument("--port",      default=SERIAL_PORT, help=f"Cổng COM (mặc định: {SERIAL_PORT})")
    parser.add_argument("--baud",      default=BAUD_RATE,   type=int, help=f"Baudrate (mặc định: {BAUD_RATE})")
    parser.add_argument("--cam-in",    default=CAMERA_IN,   type=int, help=f"Index webcam cổng Vào (mặc định: {CAMERA_IN})")
    parser.add_argument("--cam-out",   default=CAMERA_OUT,  type=int, help=f"Index webcam cổng Ra (mặc định: {CAMERA_OUT})")
    parser.add_argument("--no-verify", action="store_true",
                        help="Bỏ qua kiểm tra Backend — mở cổng ngay khi đọc được biển số")
    parser.add_argument("--manual",    action="store_true",
                        help="Chế độ manual: nhấn phím 'c' để giả lập tín hiệu IR")
    return parser.parse_args()


if __name__ == "__main__":
    args = parse_args()
    runner = GateRunner(
        port=args.port,
        baud=args.baud,
        cam_in=args.cam_in,
        cam_out=args.cam_out,
        no_verify=args.no_verify,
        manual_mode=args.manual,
    )
    runner.run()
