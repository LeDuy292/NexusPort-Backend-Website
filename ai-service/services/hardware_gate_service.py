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
os.environ["OPENCV_VIDEOIO_PRIORITY_MSMF"] = "0"  # Tắt lớp giải mã MSMF của Windows, ép dùng DirectShow chống sọc/treo
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
CAMERA_PREVIEW_TIMEOUT_SEC = 20.0


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
        cam_out_idx: int = 0
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

        self._camera_cap: Optional[cv2.VideoCapture] = None
        self._camera_lane: Optional[str] = None

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
        self._camera_state_lock = threading.Lock()
        self._camera_preview_lane: Optional[str] = None
        self._camera_preview_expires_at: Optional[float] = None
        self._capture_camera_lane: Optional[str] = None
        self._frame_ready = {"IN": threading.Event(), "OUT": threading.Event()}
        self._sensor_capture_guard = threading.Lock()
        self._sensor_capture_pending = False
        self.scan_status: Dict[str, Any] = {
            "state": "WAITING_SENSOR",
            "lane": None,
            "source": None,
        }

        # Throttle CAPTURE từ IR sensor: tránh spam khi sensor luôn LOW
        # Throttle CAPTURE từ IR sensor: tránh spam khi sensor luôn LOW
        self._last_capture_time_in: float = 0.0
        self._last_capture_time_out: float = 0.0
        self._last_capture_time: float = 0.0
        self._capture_cooldown_sec: float = 8.0  # Tối thiểu 8s giữa 2 lần CAPTURE mỗi làn

    def start(self):
        """Khởi động toàn bộ dịch vụ phần cứng trong nền."""
        if self._running:
            return
        self._running = True
        logger.info("=" * 60)
        logger.info("Khởi động HardwareGateService (ESP32 + AI; camera chờ kích hoạt)...")
        logger.info("=" * 60)

        # 1. Khởi tạo AI Pipeline
        try:
            self.pipeline = SmartPortGatePipeline()
            logger.info("[HardwareGateService] Pipeline YOLO + OCR đã sẵn sàng.")
        except Exception as e:
            logger.error("[HardwareGateService] Không thể khởi tạo Pipeline: %s", e)

        # 2. Khởi động kết nối ESP32
        self.esp32 = ESP32Controller(port=self.port, baud=self.baud)
        # Gán 2 callback riêng biệt cho từng làn
        self.esp32.start(
            on_capture_in=self._handle_esp32_capture_in,
            on_capture_out=self._handle_esp32_capture_out,
        )

        # Camera loop chỉ mở thiết bị khi có yêu cầu xem trực tiếp hoặc cảm biến báo xe.
        self._camera_thread = threading.Thread(
            target=self._camera_loop,
            name="Gate-Camera-Loop",
            daemon=True
        )
        self._camera_thread.start()
        logger.info("[HardwareGateService] Đang chờ yêu cầu bật camera hoặc tín hiệu cảm biến.")

    def stop(self):
        """Dừng dịch vụ và giải phóng tài nguyên."""
        self._running = False
        if self.esp32:
            self.esp32.stop()
        if self._camera_thread and self._camera_thread.is_alive():
            self._camera_thread.join(timeout=2)
        if self._camera_cap:
            self._camera_cap.release()
            self._camera_cap = None
        self._camera_lane = None
        logger.info("[HardwareGateService] Đã dừng toàn bộ dịch vụ phần cứng.")

    def _open_camera_by_idx(self, idx: int) -> Optional[cv2.VideoCapture]:
        """
        Mở camera bằng DirectShow + ép nén MJPG 640x480 chống nhiễu sọc/treo đen màn hình.
        """
        try:
            cap = cv2.VideoCapture(idx, cv2.CAP_DSHOW)
            if cap.isOpened():
                cap.set(cv2.CAP_PROP_FOURCC, cv2.VideoWriter_fourcc(*'MJPG'))
                cap.set(cv2.CAP_PROP_FRAME_WIDTH, 640)
                cap.set(cv2.CAP_PROP_FRAME_HEIGHT, 480)
                return cap
            cap.release()
        except Exception as e:
            logger.debug("[HardwareGateService] Không mở được camera %d qua DirectShow: %s", idx, e)

        # Fallback nhẹ nếu DirectShow không nhận
        try:
            cap = cv2.VideoCapture(idx)
            if cap.isOpened():
                cap.set(cv2.CAP_PROP_FRAME_WIDTH, 640)
                cap.set(cv2.CAP_PROP_FRAME_HEIGHT, 480)
                return cap
            cap.release()
        except Exception:
            pass
        return None

    @staticmethod
    def _normalize_lane(lane: str) -> str:
        normalized = str(lane).upper()
        if normalized not in ("IN", "OUT"):
            raise ValueError("lane phải là IN hoặc OUT")
        return normalized

    def _lane_active_locked(self, lane: str) -> bool:
        return self._selected_camera_lane_locked() == lane

    def _selected_camera_lane_locked(self) -> Optional[str]:
        if (
            self._camera_preview_lane
            and self._camera_preview_expires_at is not None
            and time.monotonic() >= self._camera_preview_expires_at
        ):
            logger.info("[HardwareGateService] Hết thời gian xem trực tiếp; tự tắt camera preview.")
            self._camera_preview_lane = None
            self._camera_preview_expires_at = None
        return self._capture_camera_lane or self._camera_preview_lane

    def _reset_lane_frame(self, lane: str):
        with self._frame_lock:
            if lane == "IN":
                self._last_frame_in = None
            else:
                self._last_frame_out = None
        self._frame_ready[lane].clear()

    def set_camera_preview(self, lane: str, enabled: bool) -> Dict[str, Any]:
        """Bật/tắt camera quan sát theo yêu cầu từ giao diện."""
        lane = self._normalize_lane(lane)
        with self._camera_state_lock:
            previous_lane = self._selected_camera_lane_locked()
            if enabled:
                self._camera_preview_lane = lane
                self._camera_preview_expires_at = time.monotonic() + CAMERA_PREVIEW_TIMEOUT_SEC
            elif self._camera_preview_lane == lane:
                self._camera_preview_lane = None
                self._camera_preview_expires_at = None
            selected_lane = self._selected_camera_lane_locked()
            if selected_lane and selected_lane != previous_lane:
                self._reset_lane_frame(selected_lane)
            is_active = selected_lane == lane
            if not is_active:
                self._reset_lane_frame(lane)

        return {
            "success": True,
            "lane": lane,
            "enabled": is_active,
            "preview_enabled": self._camera_preview_lane == lane,
            "expires_in_seconds": CAMERA_PREVIEW_TIMEOUT_SEC if enabled else 0,
            "message": "Đã bật camera quan sát" if enabled else "Đã tắt camera quan sát",
        }

    def _begin_capture(self, lane: str):
        lane = self._normalize_lane(lane)
        with self._camera_state_lock:
            self._capture_camera_lane = lane
            self._reset_lane_frame(lane)

    def _end_capture(self, lane: str):
        with self._camera_state_lock:
            if self._capture_camera_lane == lane:
                self._capture_camera_lane = None

    def _wait_for_camera_frame(self, lane: str, timeout: float = 5.0) -> bool:
        return self._frame_ready[lane].wait(timeout)

    def _camera_loop(self):
        """Chỉ mở webcam cho một làn đang xem hoặc đang được quét."""
        reconnect_interval = 3.0
        last_open_attempt = 0.0

        while self._running:
            with self._camera_state_lock:
                selected_lane = self._selected_camera_lane_locked()

            if selected_lane is None:
                if self._camera_cap:
                    self._camera_cap.release()
                    self._camera_cap = None
                self._camera_lane = None
                time.sleep(0.03)
                continue

            if selected_lane != self._camera_lane:
                if self._camera_cap:
                    self._camera_cap.release()
                self._camera_cap = None
                self._camera_lane = selected_lane
                last_open_attempt = 0.0
                self._reset_lane_frame(selected_lane)

            now = time.time()
            if self._camera_cap is None or not self._camera_cap.isOpened():
                if now - last_open_attempt >= reconnect_interval:
                    last_open_attempt = now
                    index = self.cam_in_idx if selected_lane == "IN" else self.cam_out_idx
                    logger.info("[HardwareGateService] Đang mở camera làn %s (index %d)...", selected_lane, index)
                    self._camera_cap = self._open_camera_by_idx(index)
                    if self._camera_cap:
                        logger.info("[HardwareGateService] Camera làn %s đã kết nối.", selected_lane)
                time.sleep(0.03)
                continue

            ret, frame = self._camera_cap.read()
            if ret and frame is not None:
                with self._camera_state_lock:
                    still_selected = self._selected_camera_lane_locked() == selected_lane
                if still_selected:
                    with self._frame_lock:
                        if selected_lane == "IN":
                            self._last_frame_in = frame
                        else:
                            self._last_frame_out = frame
                    self._frame_ready[selected_lane].set()
            else:
                self._camera_cap.release()
                self._camera_cap = None
                self._reset_lane_frame(selected_lane)

            time.sleep(0.01)

        if self._camera_cap:
            self._camera_cap.release()
            self._camera_cap = None
        self._camera_lane = None

    def get_frame(self, lane: str = "IN") -> np.ndarray:
        """
        Lấy khung hình mới nhất. Nếu camera chưa có, tạo khung hình đồ họa trạng thái.
        """
        with self._frame_lock:
            frame = self._last_frame_in if lane == "IN" else self._last_frame_out

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

    def _handle_esp32_capture(self, lane: str = "IN"):
        """Được gọi khi cảm biến của một làn gửi tín hiệu CAPTURE."""
        lane = self._normalize_lane(lane)
        now = time.monotonic()
        with self._sensor_capture_guard:
            last_t = self._last_capture_time_out if lane == "OUT" else self._last_capture_time_in
            elapsed = now - (last_t or self._last_capture_time)
            if self._sensor_capture_pending or self._processing_lock.locked():
                logger.info("[HardwareGateService] Đang xử lý lượt quét; bỏ qua tín hiệu cảm biến mới.")
                return
            if elapsed < self._capture_cooldown_sec:
                logger.info(
                    "[HardwareGateService] CAPTURE làn %s bị throttle (%.1fs < cooldown %.1fs) — bỏ qua.",
                    lane, elapsed, self._capture_cooldown_sec
                )
                return
            if lane == "OUT":
                self._last_capture_time_out = now
            else:
                self._last_capture_time_in = now
            self._last_capture_time = now
            self._sensor_capture_pending = True
            self.scan_status = {"state": "CAPTURING", "lane": lane, "source": "IR_SENSOR"}
        logger.info("[HardwareGateService] Nhận tín hiệu cảm biến làn %s từ ESP32.", lane)
        threading.Thread(
            target=self._process_sensor_passage,
            args=(lane,),
            daemon=True
        ).start()

    def _handle_esp32_capture_in(self):
        """Callback riêng làn Vào."""
        self._handle_esp32_capture("IN")

    def _handle_esp32_capture_out(self):
        """Callback riêng làn Ra."""
        self._handle_esp32_capture("OUT")

    def _process_sensor_passage(self, lane: str):
        try:
            self.process_passage(lane=lane, source="IR_SENSOR")
        finally:
            with self._sensor_capture_guard:
                self._sensor_capture_pending = False
                self.scan_status = {"state": "WAITING_SENSOR", "lane": None, "source": None}

    def process_passage(self, lane: str = "IN", source: str = "SENSOR") -> Dict[str, Any]:
        """
        Thực hiện toàn bộ nghiệp vụ quét, OCR, đối soát và điều khiển cổng:
        1. Lấy ảnh từ camera
        2. Chạy YOLO ONNX + Azure Vision OCR
        3. Đối soát với Booking trong Database
        4. Gửi tín hiệu OPEN / FAIL xuống ESP32
        5. Phát sự kiện Realtime lên màn hình Gate Control
        """
        lane = self._normalize_lane(lane)
        with self._processing_lock:
            self._begin_capture(lane)
            try:
                if not self._wait_for_camera_frame(lane):
                    logger.error("[process_passage] Camera làn %s không có khung hình mới.", lane)
                    if self.esp32:
                        self.esp32.send_fail()
                    return {"success": False, "error": "CAMERA_FRAME_TIMEOUT"}
                frame = self.get_frame(lane)
            finally:
                self._end_capture(lane)
            self.scan_status = {"state": "PROCESSING", "lane": lane, "source": source}
            return self._process_passage_frame(lane, source, frame)

    def _process_passage_frame(self, lane: str, source: str, frame: np.ndarray) -> Dict[str, Any]:
            logger.info("── Bắt đầu xử lý quét xe (Làn: %s, Nguồn: %s) ──", lane, source)

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
                result["lane"] = lane
            except Exception as e:
                logger.error("[process_passage] Lỗi AI Pipeline: %s", e)
                if self.esp32:
                    if lane == "IN":
                        self.esp32.send_fail_in()
                    else:
                        self.esp32.send_fail_out()
                return {"success": False, "error": str(e)}

            plate_info   = result.get("license_plate", {})
            plate_number = plate_info.get("plate_number", "")
            ocr_conf     = plate_info.get("ocr_confidence", 0.0)

            logger.info("[process_passage] Kết quả OCR: plate='%s' (conf=%.3f)", plate_number, ocr_conf)

            # 3. Kiểm tra tính hợp lệ qua Backend
            is_valid = False
            if plate_number:
                is_valid = verify_plate_with_backend(plate_number, gate_code)

            # 4. Điều khiển ESP32 — gửi lệnh đúng theo từng làn
            if is_valid:
                logger.info("[process_passage] ✅ Biển số %s HỢP LỆ → Gửi lệnh OPEN_%s xuống ESP32", plate_number, lane)
                if self.esp32:
                    if lane == "IN":
                        self.esp32.send_open_in()
                    else:
                        self.esp32.send_open_out()
                decision = "OPEN"
            else:
                logger.info("[process_passage] ❌ Biển số '%s' KHÔNG hợp lệ / không đọc được → Gửi FAIL_%s", plate_number, lane)
                if self.esp32:
                    if lane == "IN":
                        self.esp32.send_fail_in()
                    else:
                        self.esp32.send_fail_out()
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
                "lane": result.get("lane", "IN"),
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
        """Cưỡng bức mở cổng Làn Vào từ giao diện Web (backward compat)."""
        return self.manual_open_in()

    def manual_close(self) -> Dict[str, Any]:
        """Cưỡng bức đóng cổng Làn Vào từ giao diện Web (backward compat)."""
        return self.manual_close_in()

    def manual_fail(self) -> Dict[str, Any]:
        """Báo lỗi Làn Vào từ giao diện Web (backward compat)."""
        return self.manual_fail_in()

    # ── Làn Vào (IN) ──
    def manual_open_in(self) -> Dict[str, Any]:
        """Cưỡng bức mở cổng Làn Vào từ giao diện Web."""
        if self.esp32:
            self.esp32.send_open_in()
            return {"success": True, "message": "Đã gửi lệnh OPEN_IN xuống ESP32"}
        return {"success": False, "message": "ESP32 chưa kết nối"}

    def manual_close_in(self) -> Dict[str, Any]:
        """Cưỡng bức đóng cổng Làn Vào từ giao diện Web."""
        if self.esp32:
            self.esp32.send_close_in()
            return {"success": True, "message": "Đã gửi lệnh CLOSE_IN xuống ESP32"}
        return {"success": False, "message": "ESP32 chưa kết nối"}

    def manual_fail_in(self) -> Dict[str, Any]:
        """Báo lỗi Làn Vào từ giao diện Web."""
        if self.esp32:
            self.esp32.send_fail_in()
            return {"success": True, "message": "Đã gửi lệnh FAIL_IN xuống ESP32"}
        return {"success": False, "message": "ESP32 chưa kết nối"}

    # ── Làn Ra (OUT) ──
    def manual_open_out(self) -> Dict[str, Any]:
        """Cưỡng bức mở cổng Làn Ra từ giao diện Web."""
        if self.esp32:
            self.esp32.send_open_out()
            return {"success": True, "message": "Đã gửi lệnh OPEN_OUT xuống ESP32"}
        return {"success": False, "message": "ESP32 chưa kết nối"}

    def manual_close_out(self) -> Dict[str, Any]:
        """Cưỡng bức đóng cổng Làn Ra từ giao diện Web."""
        if self.esp32:
            self.esp32.send_close_out()
            return {"success": True, "message": "Đã gửi lệnh CLOSE_OUT xuống ESP32"}
        return {"success": False, "message": "ESP32 chưa kết nối"}

    def manual_fail_out(self) -> Dict[str, Any]:
        """Báo lỗi Làn Ra từ giao diện Web."""
        if self.esp32:
            self.esp32.send_fail_out()
            return {"success": True, "message": "Đã gửi lệnh FAIL_OUT xuống ESP32"}
        return {"success": False, "message": "ESP32 chưa kết nối"}

    def get_status(self) -> Dict[str, Any]:
        """Lấy toàn bộ thông tin tình trạng phần cứng phục vụ Web Dashboard."""
        with self._camera_state_lock:
            active_lane = self._selected_camera_lane_locked()
            camera_in_enabled = active_lane == "IN"
            camera_out_enabled = active_lane == "OUT"
            camera_connected = bool(
                active_lane
                and self._camera_cap
                and self._camera_cap.isOpened()
                and self._frame_ready[active_lane].is_set()
            )
        return {
            "esp32": self.esp32.status() if self.esp32 else {"connected": False},
            "camera_in": {
                "index": self.cam_in_idx,
                "enabled": camera_in_enabled,
                "connected": camera_in_enabled and camera_connected,
            },
            "camera_out": {
                "index": self.cam_out_idx,
                "enabled": camera_out_enabled,
                "connected": camera_out_enabled and camera_connected,
            },
            "ai_pipeline": {
                "ready": self.pipeline is not None,
                "azure_vision": self.pipeline.ocr_service.azure_available if self.pipeline else False,
            },
            "scan_status": self.scan_status,
            "last_event": self.last_event
        }
