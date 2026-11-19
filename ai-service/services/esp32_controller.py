"""
ESP32 Serial Controller — NexusPort Smart Gate
================================================
Module quản lý giao tiếp Serial (pyserial) với ESP32 DevKit V1 qua cổng COM7.

Giao thức:
  ESP32  → Python : "CAPTURE\\n" — cảm biến IR phát hiện xe, yêu cầu chụp + OCR
  Python → ESP32  : "OPEN\\n"   — biển số hợp lệ, mở Servo barrier
  Python → ESP32  : "FAIL\\n"   — không đọc được / biển số không hợp lệ

Thiết kế:
  - Singleton thread-safe (dùng threading.Lock)
  - Non-blocking: đọc Serial trong luồng nền, trả tín hiệu qua threading.Event
  - Tự động reconnect khi mất kết nối COM7
  - Có thể dùng độc lập hoặc import vào main.py FastAPI
"""

try:
    import serial
    import serial.tools.list_ports
except ImportError:
    serial = None
import threading
import time
import logging
from typing import Optional, Callable

logger = logging.getLogger("esp32_controller")


class ESP32Controller:
    """
    Singleton quản lý kết nối Serial với ESP32.
    Chạy một background thread liên tục đọc dữ liệu từ COM7.
    """

    _instance: Optional["ESP32Controller"] = None
    _lock = threading.Lock()

    def __new__(cls, port: str = "COM7", baud: int = 115200):
        with cls._lock:
            if cls._instance is None:
                instance = super().__new__(cls)
                instance._initialized = False
                cls._instance = instance
            return cls._instance

    def __init__(self, port: str = "COM7", baud: int = 115200):
        if self._initialized:
            return
        self._initialized = True

        self.port = port
        self.baud = baud
        self._serial: Optional[serial.Serial] = None
        self._serial_lock = threading.Lock()

        # Event được set() khi nhận CAPTURE từ ESP32
        self.capture_event = threading.Event()
        self.capture_lane = "IN"
        # Callback tùy chọn gọi khi có CAPTURE (ngoài Event)
        self._on_capture: Optional[Callable] = None
        # Callback riêng cho 2 làn
        self._on_capture_in: Optional[Callable] = None
        self._on_capture_out: Optional[Callable] = None

        self._running = False
        self._reader_thread: Optional[threading.Thread] = None

        self.connected = False
        self._logged_disconnected = False

    # ------------------------------------------------------------------
    # Public API
    # ------------------------------------------------------------------

    def start(
        self,
        on_capture: Optional[Callable] = None,
        on_capture_in: Optional[Callable] = None,
        on_capture_out: Optional[Callable] = None,
    ) -> bool:
        """
        Khởi động kết nối Serial và thread đọc nền.

        Args:
            on_capture     : Callback(lane) gọi khi nhận CAPTURE_IN/OUT hoặc CAPTURE.
            on_capture_in  : Callback() gọi khi nhận CAPTURE_IN (làn Vào).
            on_capture_out : Callback() gọi khi nhận CAPTURE_OUT (làn Ra).
        Returns:
            True nếu kết nối thành công ngay, False nếu đang retry.
        """
        self._on_capture = on_capture
        self._on_capture_in = on_capture_in
        self._on_capture_out = on_capture_out
        self._running = True

        self._reader_thread = threading.Thread(
            target=self._reader_loop, name="ESP32-Reader", daemon=True
        )
        self._reader_thread.start()
        logger.info("[ESP32] Serial reader thread started (port=%s, baud=%d)", self.port, self.baud)

        # Chờ tối đa 3s cho kết nối đầu tiên
        for _ in range(30):
            if self.connected:
                return True
            time.sleep(0.1)
        return False

    def stop(self):
        """Dừng reader thread và đóng cổng Serial."""
        self._running = False
        if self._reader_thread:
            self._reader_thread.join(timeout=3)
        self._close_serial()
        logger.info("[ESP32] Controller stopped.")

    def send_open(self):
        """Gửi lệnh OPEN xuống ESP32 → mở barrier làn Vào (backward compat)."""
        self._send("OPEN_IN")

    def send_close(self):
        """Gửi lệnh CLOSE xuống ESP32 → đóng barrier làn Vào (backward compat)."""
        self._send("CLOSE_IN")

    def send_fail(self):
        """Gửi lệnh FAIL xuống ESP32 → báo lỗi làn Vào (backward compat)."""
        self._send("FAIL_IN")

    # ── Làn Vào (IN) ──
    def send_open_in(self):
        """Gửi lệnh OPEN_IN → mở barrier làn Vào, bật LED xanh, kêu buzzer."""
        self._send("OPEN_IN")

    def send_close_in(self):
        """Gửi lệnh CLOSE_IN → đóng barrier làn Vào, bật LED đỏ."""
        self._send("CLOSE_IN")

    def send_fail_in(self):
        """Gửi lệnh FAIL_IN → báo lỗi 2 tiếng buzzer, giữ barrier làn Vào đóng."""
        self._send("FAIL_IN")

    # ── Làn Ra (OUT) ──
    def send_open_out(self):
        """Gửi lệnh OPEN_OUT → mở barrier làn Ra, bật LED xanh, kêu buzzer."""
        self._send("OPEN_OUT")

    def send_close_out(self):
        """Gửi lệnh CLOSE_OUT → đóng barrier làn Ra, bật LED đỏ."""
        self._send("CLOSE_OUT")

    def send_fail_out(self):
        """Gửi lệnh FAIL_OUT → báo lỗi 2 tiếng buzzer, giữ barrier làn Ra đóng."""
        self._send("FAIL_OUT")

    def wait_for_capture(self, timeout: float = 30.0) -> bool:
        """
        Chờ tín hiệu CAPTURE từ ESP32 (blocking, dùng trong gate_runner.py).

        Args:
            timeout: Giây tối đa chờ tín hiệu.
        Returns:
            True nếu nhận được CAPTURE trước timeout, False nếu timeout.
        """
        triggered = self.capture_event.wait(timeout=timeout)
        if triggered:
            self.capture_event.clear()
        return triggered

    def status(self) -> dict:
        """Trả về trạng thái kết nối hiện tại (dùng cho endpoint /esp32-status)."""
        return {
            "port": self.port,
            "baud_rate": self.baud,
            "connected": self.connected,
            "thread_alive": self._reader_thread.is_alive() if self._reader_thread else False,
        }

    # ------------------------------------------------------------------
    # Private
    # ------------------------------------------------------------------

    def _send(self, command: str):
        """Gửi chuỗi lệnh xuống ESP32 qua Serial (thread-safe)."""
        with self._serial_lock:
            if self._serial and self._serial.is_open:
                try:
                    payload = (command.strip() + "\n").encode("utf-8")
                    self._serial.write(payload)
                    self._serial.flush()
                    logger.info("[ESP32] → Sent: %s", command)
                except Exception as e:
                    logger.error("[ESP32] Send error: %s", e)
                    # Không đóng serial ngay khi chỉ bị timeout nhẹ
                    if "closed" in str(e).lower() or "device" in str(e).lower():
                        self._close_serial()
                        self.connected = False
            else:
                logger.warning("[ESP32] Cannot send '%s': port not open.", command)

    def _find_esp32_port(self) -> Optional[str]:
        """Tự động dò tìm cổng Serial của ESP32 (chip CH340, CP210x, FTDI)."""
        if serial is None:
            return None
        try:
            ports = list(serial.tools.list_ports.comports())
            for p in ports:
                desc = (p.description or "").upper()
                hwid = (p.hwid or "").upper()
                # Nhận diện CH340, CP210x, ESP32 hoặc USB Serial
                if any(k in desc or k in hwid for k in ["CH340", "CP210", "1A86:7523", "ESP32", "USB-SERIAL", "USB SERIAL"]):
                    return p.device
            # Nếu chỉ có 1 cổng COM khả dụng không phải Bluetooth
            non_bt = [p.device for p in ports if "BLUETOOTH" not in (p.description or "").upper()]
            if len(non_bt) == 1:
                return non_bt[0]
        except Exception as e:
            logger.debug("[ESP32] Error auto-detecting port: %s", e)
        return None

    def _open_serial(self) -> bool:
        """Mở cổng Serial, tự dò tìm cổng nếu cổng chỉ định không tồn tại."""
        if serial is None:
            logger.warning("[ESP32] Thư viện pyserial chưa được cài đặt. Chạy chế độ mô phỏng / không Serial.")
            return False
        target_port = self.port
        try:
            with self._serial_lock:
                self._serial = serial.Serial(
                    port=target_port,
                    baudrate=self.baud,
                    timeout=1,
                    write_timeout=2,
                )
                try:
                    self._serial.dtr = False
                    self._serial.rts = False
                except Exception:
                    pass
            time.sleep(2)  # Chờ ESP32 reset sau khi mở cổng
            self.connected = True
            self._logged_disconnected = False
            logger.info("[ESP32] Đã kết nối thành công tới %s @ %d baud", target_port, self.baud)
            return True
        except serial.SerialException:
            # Thử tự động dò cổng khác nếu cổng mặc định (như COM7) không mở được
            detected_port = self._find_esp32_port()
            if detected_port and detected_port != target_port:
                logger.info("[ESP32] Không tìm thấy %s, tự động chuyển sang %s (CH340/CP210x detected)", target_port, detected_port)
                try:
                    with self._serial_lock:
                        self._serial = serial.Serial(
                            port=detected_port,
                            baudrate=self.baud,
                            timeout=1,
                            write_timeout=2,
                        )
                    self.port = detected_port
                    time.sleep(2)
                    self.connected = True
                    self._logged_disconnected = False
                    logger.info("[ESP32] Đã kết nối thành công tới %s @ %d baud", detected_port, self.baud)
                    return True
                except serial.SerialException as e2:
                    if not self._logged_disconnected:
                        logger.warning("[ESP32] Không thể mở cổng tự dò %s: %s", detected_port, e2)
                        self._logged_disconnected = True
                    else:
                        logger.debug("[ESP32] Cannot open auto-detected port %s: %s", detected_port, e2)
            else:
                if not self._logged_disconnected:
                    logger.warning(
                        "[ESP32] Chưa cắm hoặc không phát hiện phần cứng ESP32 trên cổng %s. "
                        "Dịch vụ AI Smart Gate vẫn hoạt động bình thường qua API / Web Camera / mô phỏng phần mềm. "
                        "Hệ thống sẽ tự động đồng bộ ngay khi cắm cáp USB ESP32.",
                        target_port
                    )
                    self._logged_disconnected = True
                else:
                    logger.debug("[ESP32] Đang chờ kết nối ESP32 trên cổng %s...", target_port)
            self.connected = False
            return False

    def _close_serial(self):
        """Đóng cổng Serial an toàn."""
        with self._serial_lock:
            if self._serial:
                try:
                    self._serial.close()
                except Exception:
                    pass
                self._serial = None

    def _reader_loop(self):
        """
        Background thread: Liên tục đọc Serial, tự reconnect khi mất kết nối.
        Mỗi khi nhận "CAPTURE" → set capture_event và gọi callback nếu có.
        """
        while self._running:
            if not self.connected:
                if not self._logged_disconnected:
                    logger.info("[ESP32] Đang tìm kiếm cổng kết nối phần cứng %s ...", self.port)
                else:
                    logger.debug("[ESP32] Polling for connection on %s ...", self.port)
                try:
                    if not self._open_serial():
                        time.sleep(5)  # Chờ 5s trước khi thử lại
                        continue
                except Exception as e:
                    logger.debug("[ESP32] Error opening serial port: %s", e)
                    time.sleep(5)
                    continue

            try:
                with self._serial_lock:
                    ser = self._serial

                if ser is None or not ser.is_open:
                    self.connected = False
                    continue

                raw = ser.readline()
                if not raw:
                    continue

                line = raw.decode("utf-8", errors="ignore").strip()
                if not line:
                    continue

                logger.info("[ESP32] ← Received: %s", line)

                if line in ("CAPTURE", "CAPTURE:IN", "CAPTURE_IN", "CAPTURE:OUT", "CAPTURE_OUT"):
                    lane = "OUT" if line in ("CAPTURE:OUT", "CAPTURE_OUT") else "IN"
                    logger.info("[ESP32] CAPTURE signal received for lane %s — triggering OCR", lane)
                    self.capture_lane = lane
                    self.capture_event.set()
                    # Callback on_capture(lane)
                    if self._on_capture:
                        try:
                            self._on_capture(lane)
                        except Exception as cb_err:
                            logger.error("[ESP32] on_capture callback error: %s", cb_err)
                    # Callback theo từng làn
                    if lane == "IN" and self._on_capture_in:
                        try:
                            self._on_capture_in()
                        except Exception as cb_err:
                            logger.error("[ESP32] on_capture_in callback error: %s", cb_err)
                    elif lane == "OUT" and self._on_capture_out:
                        try:
                            self._on_capture_out()
                        except Exception as cb_err:
                            logger.error("[ESP32] on_capture_out callback error: %s", cb_err)

            except serial.SerialException as e:
                logger.error("[ESP32] Serial read error: %s — will reconnect", e)
                self._close_serial()
                self.connected = False
                time.sleep(3)
            except Exception as e:
                logger.error("[ESP32] Unexpected error in reader loop: %s", e)
                time.sleep(1)
