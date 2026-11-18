/*
 * NexusPort Smart Gate — Firmware ESP32 (Dual Lane: IN + OUT)
 * ============================================================
 * Làn Vào (IN)  : IR=D19, Servo=D13, LED Đỏ=D25, LED Xanh=D26
 * Làn Ra  (OUT) : IR=D4,  Servo=D14, LED Đỏ=D32, LED Xanh=D33
 * Dùng chung    : Buzzer=D12
 *
 * Giao thức Serial 115200 baud:
 *   ESP32 → PC : CAPTURE_IN, CAPTURE_OUT
 *   PC → ESP32 : OPEN_IN, CLOSE_IN, FAIL_IN, OPEN_OUT, CLOSE_OUT, FAIL_OUT
 *   ESP32 → PC : STATUS:OPENED_IN, STATUS:CLOSED_IN, STATUS:OPENED_OUT, STATUS:CLOSED_OUT
 */

#include <ESP32Servo.h>

// ══════════════════════════════════════════════════════════
//  Khai báo chân phần cứng
// ══════════════════════════════════════════════════════════

// ── Làn Vào (IN) ──
const int irPinIN     = 19;
const int servoPinIN  = 13;
const int ledRedIN    = 25;
const int ledGrnIN    = 26;

// ── Làn Ra (OUT) ──
const int irPinOUT    = 4;
const int servoPinOUT = 14;
const int ledRedOUT   = 32;
const int ledGrnOUT   = 33;

// ── Dùng chung ──
const int buzzerPin   = 12;

// ══════════════════════════════════════════════════════════
//  Servo objects
// ══════════════════════════════════════════════════════════
Servo servoIN;
Servo servoOUT;

// ══════════════════════════════════════════════════════════
//  Trạng thái độc lập mỗi làn
// ══════════════════════════════════════════════════════════
bool gateOpenIN       = false;
bool waitingOCR_IN    = false;
unsigned long reqTimeIN    = 0;
unsigned long openedAtIN   = 0;
unsigned long lastFailIN   = 0;

bool gateOpenOUT      = false;
bool waitingOCR_OUT   = false;
unsigned long reqTimeOUT   = 0;
unsigned long openedAtOUT  = 0;
unsigned long lastFailOUT  = 0;

// ══════════════════════════════════════════════════════════
//  Hằng thời gian
// ══════════════════════════════════════════════════════════
const unsigned long AUTO_CLOSE_MS    = 30000UL; // 30 giây tự đóng
const unsigned long FAIL_COOLDOWN_MS =  8000UL; // 8 giây cooldown sau FAIL
const unsigned long OCR_TIMEOUT_MS   = 10000UL; // 10 giây timeout chờ OCR
const unsigned long IR_DEBOUNCE_MS   =   500UL; // Chống dội IR

// ══════════════════════════════════════════════════════════
//  Hàm tiện ích: Kêu còi
// ══════════════════════════════════════════════════════════
void beep(int times, int durationMs, int pauseMs) {
  for (int i = 0; i < times; i++) {
    digitalWrite(buzzerPin, HIGH);
    delay(durationMs);
    digitalWrite(buzzerPin, LOW);
    if (i < times - 1) delay(pauseMs);
  }
}

// ══════════════════════════════════════════════════════════
//  Làn Vào (IN) — Mở & Đóng
// ══════════════════════════════════════════════════════════
void openGateIN() {
  if (gateOpenIN) {
    // Đã mở rồi → gia hạn thêm 30s, không xoay thêm
    gateOpenedAt = millis();  // alias không dùng, dùng openedAtIN
    openedAtIN = millis();
    return;
  }

  gateOpenIN  = true;
  openedAtIN  = millis();

  // Đèn: Xanh sáng, Đỏ tắt
  digitalWrite(ledRedIN, LOW);
  digitalWrite(ledGrnIN, HIGH);

  // Còi 1 bíp ngắn
  beep(1, 200, 0);

  // Servo lên 90° và GIỮ NGUYÊN
  servoIN.write(90);

  Serial.println("STATUS:OPENED_IN");
}

void closeGateIN() {
  if (!gateOpenIN) return;

  gateOpenIN = false;

  // Servo về 0°
  servoIN.write(0);

  // Đèn: Đỏ sáng, Xanh tắt
  digitalWrite(ledGrnIN, LOW);
  digitalWrite(ledRedIN, HIGH);

  Serial.println("STATUS:CLOSED_IN");
}

// ══════════════════════════════════════════════════════════
//  Làn Ra (OUT) — Mở & Đóng
// ══════════════════════════════════════════════════════════
void openGateOUT() {
  if (gateOpenOUT) {
    openedAtOUT = millis();
    return;
  }

  gateOpenOUT  = true;
  openedAtOUT  = millis();

  // Đèn: Xanh sáng, Đỏ tắt
  digitalWrite(ledRedOUT, LOW);
  digitalWrite(ledGrnOUT, HIGH);

  // Còi 1 bíp ngắn
  beep(1, 200, 0);

  // Servo lên 90° và GIỮ NGUYÊN
  servoOUT.write(90);

  Serial.println("STATUS:OPENED_OUT");
}

void closeGateOUT() {
  if (!gateOpenOUT) return;

  gateOpenOUT = false;

  // Servo về 0°
  servoOUT.write(0);

  // Đèn: Đỏ sáng, Xanh tắt
  digitalWrite(ledGrnOUT, LOW);
  digitalWrite(ledRedOUT, HIGH);

  Serial.println("STATUS:CLOSED_OUT");
}

// ══════════════════════════════════════════════════════════
//  FAIL handler (kêu còi 2 tiếng, không xoay servo)
// ══════════════════════════════════════════════════════════
void handleFailIN() {
  beep(2, 100, 100);
  waitingOCR_IN = false;
  lastFailIN = millis();
}

void handleFailOUT() {
  beep(2, 100, 100);
  waitingOCR_OUT = false;
  lastFailOUT = millis();
}

// ══════════════════════════════════════════════════════════
//  SETUP
// ══════════════════════════════════════════════════════════
void setup() {
  Serial.begin(115200);

  // Chân IR (INPUT_PULLUP → LOW khi xe chắn ngang)
  pinMode(irPinIN,  INPUT_PULLUP);
  pinMode(irPinOUT, INPUT_PULLUP);

  // Chân LED & Buzzer
  pinMode(ledRedIN,  OUTPUT);
  pinMode(ledGrnIN,  OUTPUT);
  pinMode(ledRedOUT, OUTPUT);
  pinMode(ledGrnOUT, OUTPUT);
  pinMode(buzzerPin, OUTPUT);

  // Trạng thái ban đầu: Tất cả đèn đỏ sáng (cổng đóng)
  digitalWrite(ledRedIN,  HIGH);
  digitalWrite(ledGrnIN,  LOW);
  digitalWrite(ledRedOUT, HIGH);
  digitalWrite(ledGrnOUT, LOW);
  digitalWrite(buzzerPin, LOW);

  // Servo: Gán chân với dải xung SG90 chuẩn (500–2400µs)
  servoIN.attach(servoPinIN,   500, 2400);
  servoOUT.attach(servoPinOUT, 500, 2400);

  // Vị trí ban đầu: 0° (đóng)
  servoIN.write(0);
  servoOUT.write(0);

  Serial.println("BOOT:OK — NexusPort Dual-Lane Gate Ready");
}

// ══════════════════════════════════════════════════════════
//  LOOP
// ══════════════════════════════════════════════════════════

// Biến alias cho openGateIN (để tránh lỗi khai báo bên trên)
unsigned long gateOpenedAt = 0; // unused — xem openedAtIN/OUT

void loop() {
  unsigned long now = millis();

  // ──────────────────────────────────────────────────────
  //  1. TỰ ĐỘNG ĐÓNG SAU 30 GIÂY (mỗi làn độc lập)
  // ──────────────────────────────────────────────────────
  if (gateOpenIN  && (now - openedAtIN  >= AUTO_CLOSE_MS)) closeGateIN();
  if (gateOpenOUT && (now - openedAtOUT >= AUTO_CLOSE_MS)) closeGateOUT();

  // ──────────────────────────────────────────────────────
  //  2. CẢM BIẾN IR — Kích hoạt CAPTURE khi phát hiện xe
  // ──────────────────────────────────────────────────────

  // Làn Vào
  bool irIN  = (digitalRead(irPinIN)  == LOW);
  bool coolIN  = (now - lastFailIN  > FAIL_COOLDOWN_MS);
  if (irIN && !gateOpenIN && !waitingOCR_IN && coolIN) {
    Serial.println("CAPTURE_IN");
    waitingOCR_IN = true;
    reqTimeIN = now;
    delay(IR_DEBOUNCE_MS); // Chống dội
  }

  // Làn Ra
  bool irOUT = (digitalRead(irPinOUT) == LOW);
  bool coolOUT = (now - lastFailOUT > FAIL_COOLDOWN_MS);
  if (irOUT && !gateOpenOUT && !waitingOCR_OUT && coolOUT) {
    Serial.println("CAPTURE_OUT");
    waitingOCR_OUT = true;
    reqTimeOUT = now;
    delay(IR_DEBOUNCE_MS);
  }

  // ──────────────────────────────────────────────────────
  //  3. TIMEOUT CHỜ OCR (10 giây)
  // ──────────────────────────────────────────────────────
  if (waitingOCR_IN  && (now - reqTimeIN  > OCR_TIMEOUT_MS)) {
    waitingOCR_IN  = false;
    lastFailIN  = now;
  }
  if (waitingOCR_OUT && (now - reqTimeOUT > OCR_TIMEOUT_MS)) {
    waitingOCR_OUT = false;
    lastFailOUT = now;
  }

  // ──────────────────────────────────────────────────────
  //  4. LẮNG NGHE LỆNH TỪ MÁY TÍNH
  // ──────────────────────────────────────────────────────
  if (Serial.available() > 0) {
    String command = Serial.readStringUntil('\n');
    command.trim();

    // ── Làn Vào ──
    if (command == "OPEN_IN") {
      openGateIN();
      waitingOCR_IN = false;

    } else if (command == "CLOSE_IN") {
      closeGateIN();
      waitingOCR_IN = false;

    } else if (command == "FAIL_IN") {
      handleFailIN();

    // ── Làn Ra ──
    } else if (command == "OPEN_OUT") {
      openGateOUT();
      waitingOCR_OUT = false;

    } else if (command == "CLOSE_OUT") {
      closeGateOUT();
      waitingOCR_OUT = false;

    } else if (command == "FAIL_OUT") {
      handleFailOUT();

    // ── Backward compat (1 làn cũ → IN) ──
    } else if (command == "OPEN") {
      openGateIN();
      waitingOCR_IN = false;

    } else if (command == "CLOSE") {
      closeGateIN();
      waitingOCR_IN = false;

    } else if (command == "FAIL") {
      handleFailIN();

    // ── Ping ──
    } else if (command == "PING") {
      Serial.println("PONG");
    }
  }
}
