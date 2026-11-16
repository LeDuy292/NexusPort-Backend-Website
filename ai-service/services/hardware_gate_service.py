"""
HardwareGateService — NexusPort Smart Gate
===========================================
Dịch vụ tự động quản lý phần cứng Cổng Cảng thông minh (ESP32 + Webcam + AI OCR):
- Quản lý kết nối Serial với ESP32 (Auto-detect chip CH340 / COM8).
- Lắng nghe sự kiện cảm biến hồng ngoại 'CAPTURE' từ ESP32.
- Quản lý luồng video camera cho 2 làn (Vào / Ra).
- Cung cấp frame ảnh phục vụ MJPEG live streaming lên Web UI.
- Tự động gọi SmartPortGatePipeline để OCR biển số xe (Azure Vision + YOLO ONNX).
- Đối soát biển số với Backend Node.js (/api/gate/verify-plate).
- Ra quyết định điều khiển Servo Barrier: 'OPEN' hoặc 'FAIL'.
- Phát sự kiện Realtime tới màn hình Gate Officer qua Socket.IO.
"""

import os
import sys
import time
import logging
import threading
import base64
from typing import Optional, Dict, Any, Generator

import cv2
import numpy as np
import requests
from dotenv import load_dotenv

from .pipeline import SmartPortGatePipeline
from .esp32_controller import ESP32Controller

logger = logging.getLogger("hardware_gate_service")

# ── Cấu hình ─────────────────────────────────────────────────────────────────
EXPRESS_URL   = os.getenv("EXPRESS_BASE_URL", "http://localhost:3001").rstrip("/")
GATE_CODE_IN  = os.getenv("GATE_CODE_IN",  "GATE_IN_A")
LANE_CODE_IN  = os.getenv("LANE_CODE_IN",  "LANE_01")
CAMERA_ID_IN  = os.getenv("CAMERA_ID_IN",  "CAM_GATE_IN_01")

GATE_CODE_OUT = os.getenv("GATE_CODE_OUT", "GATE_OUT_A")
LANE_CODE_OUT = os.getenv("LANE_CODE_OUT", "LANE_02")
CAMERA_ID_OUT = os.getenv("CAMERA_ID_OUT", "CAM_GATE_OUT_01")


def verify_plate_with_backend(plate_number: str, gate_code: str) -> bool:
    """
    Gọi API NexusPort Node.js để kiểm tra biển số xe có booking hợp lệ không.
    GET /api/gate/verify-plate?plate=...
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
                "[verify] plate=%s gate=%s → valid=%s (backend: %s)",
                plate_number, gate_code, is_valid, data.get("booking", {}).get("bookingCode", "None")
            )
            return bool(is_valid)
        else:
            logger.warning("[verify] Backend returned HTTP %d for plate=%s", resp.status_code, plate_number)
            return False
    except requests.exceptions.ConnectionError:
        logger.warning("[verify] Cannot reach backend at %s — fallback FAIL", EXPRESS_URL)
        return False
    except Exception as e:
        logger.error("[verify] Error verifying plate: %s", e)
        return False


class HardwareGateService:
    """
    Singleton quản lý toàn bộ luồng phần cứng và camera của trạm cổng.
    """
    _instance: Optional["HardwareGateService"] = None
    _lock = threading.Lock()

    def __new__(cls, *args, **kwargs):
        with cls._lock:
            if cls._instance is None:
                instance = super().__new__(cls)
                instance._initialized = False
                cls._instance = instance
            return cls._instance

    def __init__(
        self,
        port: str = "COM8",
        baud: int = 115200,
        cam_in_idx: int = 0,
        cam_out_idx: int = 1
    ):
        if self._initialized:
            return
        self._initialized = True

        self.port = port
        self.baud = baud
        self.cam_in_idx = cam_in_idx
        self.cam_out_idx = cam_out_idx

        self.pipeline: Optional[SmartPortGatePipeline] = None
        self.esp32: Optional[ESP32Controller] = None

        self._cap_in: Optional[cv2.VideoCapture] = None
        self._cap_out: Optional[cv2.VideoCapture] = None

        # Khung hình mới nhất được cache (dùng để stream và OCR)
        self._last_frame_in: Optional[np.ndarray] = None
        self._last_frame_out: Optional[np.ndarray] = None
        self._frame_lock = threading.Lock()

        # Trạng thái gần nhất để hiển thị dashboard
        self.last_event: Dict[str, Any] = {
            "timestamp": None,
            "plate": None,
            "status": "IDLE",
            "decision": None,
            "lane": None,
            "source": None
        }

        self._running = False
        self._camera_thread: Optional[threading.Thread] = None
        self._processing_lock = threading.Lock()

        # Throttle CAPTURE từ IR sensor: tránh spam khi sensor luôn LOW
        self._last_capture_time: float = 0.0
        self._capture_cooldown_sec: float = 8.0  # Tối thiểu 8s giữa 2 lần CAPTURE

    def start(self):
        """Khởi động toàn bộ dịch vụ phần cứng trong nền."""
        if self._running:
            return
        self._running = True
        logger.info("=" * 60)
        logger.info("Khởi động HardwareGateService (ESP32 + Camera + AI)...")
        logger.info("=" * 60)

        # 1. Khởi tạo AI Pipeline
        try:
            self.pipeline = SmartPortGatePipeline()
            logger.info("[HardwareGateService] Pipeline YOLO + OCR đã sẵn sàng.")
        except Exception as e:
            logger.error("[HardwareGateService] Không thể khởi tạo Pipeline: %s", e)

        # 2. Khởi động kết nối ESP32
        self.esp32 = ESP32Controller(port=self.port, baud=self.baud)
        # Gán callback xử lý khi nhận được CAPTURE từ ESP32
        self.esp32.start(on_capture=self._handle_esp32_capture)

        # 3. Mở Camera và bắt đầu luồng đọc video liên tục
        self._open_cameras()
        self._camera_thread = threading.Thread(
            target=self._camera_loop,
            name="Gate-Camera-Loop",
            daemon=True
        )
        self._camera_thread.start()
        logger.info("[HardwareGateService] Luồng Camera background đã khởi chạy.")

    def stop(self):
        """Dừng dịch vụ và giải phóng tài nguyên."""
        self._running = False
        if self.esp32:
            self.esp32.stop()
        if self._cap_in:
            self._cap_in.release()
        if self._cap_out:
            self._cap_out.release()
        logger.info("[HardwareGateService] Đã dừng toàn bộ dịch vụ phần cứng.")

    def _open_camera_by_idx(self, idx: int) -> Optional[cv2.VideoCapture]:
        """Thử mở camera bằng nhiều backend DirectShow, MSMF hoặc Any."""
        for backend in [cv2.CAP_DSHOW, cv2.CAP_MSMF, cv2.CAP_ANY]:
            try:
                cap = cv2.VideoCapture(idx, backend)
                if cap.isOpened():
                    return cap
                cap.release()
            except Exception:
                pass
        return None

    def _open_cameras(self):
        """Mở webcam 2 làn cổng."""
        logger.info("[HardwareGateService] Mở camera làn Vào (index %d)...", self.cam_in_idx)
        self._cap_in = self._open_camera_by_idx(self.cam_in_idx)
        if self._cap_in and self._cap_in.isOpened():
            logger.info("[HardwareGateService] Camera làn Vào (index %d) đã kết nối.", self.cam_in_idx)
        else:
            logger.warning("[HardwareGateService] Camera làn Vào chưa kết nối (sẽ tự thử lại khi cắm).")

        self._cap_out = self._open_camera_by_idx(self.cam_out_idx)
        if self._cap_out and self._cap_out.isOpened():
            logger.info("[HardwareGateService] Camera làn Ra (index %d) đã kết nối.", self.cam_out_idx)
        else:
            logger.debug("[HardwareGateService] Camera làn Ra chưa cắm (chế độ 1 làn).")

    def _camera_loop(self):
        """Vòng lặp đọc khung hình camera liên tục trong nền."""
        reconnect_interval = 5.0
        last_reconnect_check = time.time()

        while self._running:
            now = time.time()

            # Tự động kết nối lại nếu camera chưa mở
            if (self._cap_in is None or not self._cap_in.isOpened()) and (now - last_reconnect_check > reconnect_interval):
                last_reconnect_check = now
                self._cap_in = self._open_camera_by_idx(self.cam_in_idx)

            # Đọc camera làn Vào
            if self._cap_in and self._cap_in.isOpened():
                ret, frame = self._cap_in.read()
                if ret and frame is not None:
                    with self._frame_lock:
                        self._last_frame_in = frame
                else:
                    self._cap_in.release()
                    self._cap_in = None

            # Đọc camera làn Ra
            if self._cap_out and self._cap_out.isOpened():
                ret, frame = self._cap_out.read()
                if ret and frame is not None:
                    with self._frame_lock:
                        self._last_frame_out = frame

            time.sleep(0.03)  # ~30 FPS

    def get_frame(self, lane: str = "IN") -> np.ndarray:
        """
        Lấy khung hình mới nhất. Nếu camera chưa có, tạo khung hình đồ họa trạng thái.
        """
        with self._frame_lock:
            frame = self._last_frame_in if lane == "IN" else (self._last_frame_out or self._last_frame_in)

        if frame is not None:
            return frame.copy()

        # Tạo frame giả lập trạng thái hiển thị chuyên nghiệp
        placeholder = np.zeros((480, 640, 3), dtype=np.uint8)
        placeholder[:] = (26, 32, 44)  # Dark slate background

        cv2.putText(placeholder, "NEXUSPORT AI SMART GATE", (120, 160),
                    cv2.FONT_HERSHEY_DUPLEX, 0.9, (0, 200, 255), 2)
        cv2.putText(placeholder, f"LANE: {lane} (CAM {self.cam_in_idx if lane=='IN' else self.cam_out_idx})",
                    (200, 210), cv2.FONT_HERSHEY_SIMPLEX, 0.7, (200, 200, 200), 2)

        esp_text = f"ESP32: Connected ({self.esp32.port})" if (self.esp32 and self.esp32.connected) else "ESP32: Disconnected"
        esp_color = (0, 255, 100) if (self.esp32 and self.esp32.connected) else (80, 80, 240)
        cv2.putText(placeholder, esp_text, (180, 260), cv2.FONT_HERSHEY_SIMPLEX, 0.7, esp_color, 2)

        cv2.putText(placeholder, "CAMERA WAITING FOR CONNECTION...", (130, 320),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.6, (140, 140, 140), 1)

        t_str = time.strftime("%Y-%m-%d %H:%M:%S")
        cv2.putText(placeholder, t_str, (210, 380),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.6, (100, 150, 200), 1)
        return placeholder

    def generate_mjpeg_stream(self, lane: str = "IN") -> Generator[bytes, None, None]:
        """Generator trả về luồng video MJPEG phục vụ thẻ <img> trên Web UI."""
        while self._running:
            frame = self.get_frame(lane)

            # Vẽ thanh trạng thái nhỏ phía dưới khung hình
            esp_status = "ESP32 OK" if (self.esp32 and self.esp32.connected) else "ESP32 Offline"
            status_color = (0, 255, 0) if (self.esp32 and self.esp32.connected) else (0, 0, 255)
            cv2.putText(frame, f"LANE {lane} | {esp_status}", (15, 30),
                        cv2.FONT_HERSHEY_SIMPLEX, 0.65, status_color, 2)

            success, buffer = cv2.imencode(".jpg", frame, [int(cv2.IMWRITE_JPEG_QUALITY), 80])
            if not success:
                time.sleep(0.05)
                continue

            frame_bytes = buffer.tobytes()
            yield (b"--frame\r\n"
                   b"Content-Type: image/jpeg\r\n\r\n" + frame_bytes + b"\r\n")
            time.sleep(0.04)

    def _handle_esp32_capture(self):
        """Được gọi khi cảm biến hồng ngoại trên ESP32 kích hoạt 'CAPTURE'."""
        import time
        now = time.time()
        elapsed = now - self._last_capture_time
        if elapsed < self._capture_cooldown_sec:
            logger.info(
                "[HardwareGateService] CAPTURE bị throttle (%.1fs < cooldown %.1fs) — bỏ qua.",
                elapsed, self._capture_cooldown_sec
            )
            return
        self._last_capture_time = now
        logger.info("[HardwareGateService] Tín hiệu IR CAPTURE nhận được từ ESP32!")
        threading.Thread(
            target=self.process_passage,
            args=("IN", "IR_SENSOR"),
            daemon=True
        ).start()

    def process_passage(self, lane: str = "IN", source: str = "WEB_MANUAL") -> Dict[str, Any]:
        """
        Thực hiện toàn bộ nghiệp vụ quét, OCR, đối soát và điều khiển cổng:
        1. Lấy ảnh từ camera
        2. Chạy YOLO ONNX + Azure Vision OCR
        3. Đối soát với Booking trong Database
        4. Gửi tín hiệu OPEN / FAIL xuống ESP32
        5. Phát sự kiện Realtime lên màn hình Gate Control
        """
        with self._processing_lock:
            logger.info("── Bắt đầu xử lý quét xe (Làn: %s, Nguồn: %s) ──", lane, source)
            frame = self.get_frame(lane)

            success, buf = cv2.imencode(".jpg", frame, [int(cv2.IMWRITE_JPEG_QUALITY), 95])
            if not success:
                logger.error("[process_passage] Không thể mã hóa khung hình.")
                if self.esp32:
                    self.esp32.send_fail()
                return {"success": False, "error": "FRAME_ENCODE_FAILED"}

            image_bytes = buf.tobytes()
            gate_code = GATE_CODE_IN if lane == "IN" else GATE_CODE_OUT
            lane_code = LANE_CODE_IN if lane == "IN" else LANE_CODE_OUT
            camera_id = CAMERA_ID_IN if lane == "IN" else CAMERA_ID_OUT

            # 2. Xử lý nhận diện AI Pipeline
            try:
                result = self.pipeline.process_image(
                    image_bytes=image_bytes,
                    camera_id=camera_id,
                    lane_code=lane_code,
                    gate_code=gate_code,
                )
            except Exception as e:
                logger.error("[process_passage] Lỗi AI Pipeline: %s", e)
                if self.esp32:
                    self.esp32.send_fail()
                return {"success": False, "error": str(e)}

            plate_info   = result.get("license_plate", {})
            plate_number = plate_info.get("plate_number", "")
            ocr_conf     = plate_info.get("ocr_confidence", 0.0)

            logger.info("[process_passage] Kết quả OCR: plate='%s' (conf=%.3f)", plate_number, ocr_conf)

            # 3. Kiểm tra tính hợp lệ qua Backend
            is_valid = False
            if plate_number:
                is_valid = verify_plate_with_backend(plate_number, gate_code)

            # 4. Điều khiển ESP32
            if is_valid:
                logger.info("[process_passage] ✅ Biển số %s HỢP LỆ → Gửi lệnh OPEN xuống ESP32", plate_number)
                if self.esp32:
                    self.esp32.send_open()
                decision = "OPEN"
            else:
                logger.info("[process_passage] ❌ Biển số '%s' KHÔNG hợp lệ / không đọc được → Gửi FAIL", plate_number)
                if self.esp32:
                    self.esp32.send_fail()
                decision = "FAIL"

            # 5. Cập nhật trạng thái
            self.last_event = {
                "timestamp": time.strftime("%Y-%m-%dT%H:%M:%SZ"),
                "plate": plate_number,
                "confidence": ocr_conf,
                "status": "PROCESSED",
                "decision": decision,
                "lane": lane,
                "source": source
            }

            # 6. Phát Realtime Socket.IO tới màn hình Cổng
            self._broadcast_event(result, image_bytes, decision)

            return {
                "success": True,
                "plate_number": plate_number,
                "confidence": ocr_conf,
                "decision": decision,
                "gate_code": gate_code,
                "lane_code": lane_code
            }

    def _broadcast_event(self, result: dict, image_bytes: bytes, decision: str):
        """Phát sự kiện lên Express Node.js Backend để đẩy Socket.IO."""
        try:
            plate_obj = result.get("license_plate", {})
            plate = plate_obj.get("plate_number") or ""

            overview_b64 = base64.b64encode(image_bytes).decode("utf-8")
            crop_img = plate_obj.get("plate_image_base64")

            payload = {
                "gateCode": result.get("gate_code", GATE_CODE_IN),
                "laneCode": result.get("lane_code", LANE_CODE_IN),
                "cameraId": result.get("camera_id", CAMERA_ID_IN),
                "detectedPlate": plate,
                "plateConfidence": plate_obj.get("ocr_confidence", 0.0),
                "vehicleDetected": True,
                "vehicleType": result.get("vehicle", {}).get("type", "container_truck"),
                "vehiclePlateImageUrl": f"data:image/jpeg;base64,{crop_img}" if crop_img and not crop_img.startswith("data:") else crop_img,
                "overviewImageUrl": f"data:image/jpeg;base64,{overview_b64}",
                "timestamp": result.get("recognized_at") or time.strftime("%Y-%m-%dT%H:%M:%SZ"),
                "source": "AI_Hardware_Gate",
                "decision": decision
            }

            url = f"{EXPRESS_URL}/api/gate/iot-event"
            requests.post(url, json=payload, timeout=2.0)
            logger.info("[broadcast] Đã gửi thông tin xe '%s' (%s) lên Gate Control UI qua Socket.IO", plate, decision)
        except Exception as e:
            logger.warning("[broadcast] Không gửi được event lên Backend: %s", e)

    def manual_open(self) -> Dict[str, Any]:
        """Cưỡng bức mở cổng từ giao diện Web."""
        if self.esp32:
            self.esp32.send_open()
            return {"success": True, "message": "Đã gửi lệnh OPEN xuống ESP32"}
        return {"success": False, "message": "ESP32 chưa kết nối"}

    def manual_fail(self) -> Dict[str, Any]:
        """Báo lỗi / giữ cổng đóng từ giao diện Web."""
        if self.esp32:
            self.esp32.send_fail()
            return {"success": True, "message": "Đã gửi lệnh FAIL xuống ESP32"}
        return {"success": False, "message": "ESP32 chưa kết nối"}

    def get_status(self) -> Dict[str, Any]:
        """Lấy toàn bộ thông tin tình trạng phần cứng phục vụ Web Dashboard."""
        return {
            "esp32": self.esp32.status() if self.esp32 else {"connected": False},
            "camera_in": {
                "index": self.cam_in_idx,
                "connected": self._cap_in is not None and self._cap_in.isOpened(),
            },
            "camera_out": {
                "index": self.cam_out_idx,
                "connected": self._cap_out is not None and self._cap_out.isOpened(),
            },
            "ai_pipeline": {
                "ready": self.pipeline is not None,
                "azure_vision": self.pipeline.ocr_service.azure_available if self.pipeline else False,
            },
            "last_event": self.last_event
        }
