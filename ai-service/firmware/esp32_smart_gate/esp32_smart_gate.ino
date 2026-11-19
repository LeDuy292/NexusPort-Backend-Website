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
//  Khai báo chân phần cứng (Khớp 100% yêu cầu)
// ══════════════════════════════════════════════════════════

// ── Làn Vào (IN) ──
const int irPinIN     = 19; // Cảm biến IR cổng Vào: D19
const int servoPinIN  = 13; // Servo cổng Vào: D13
const int ledRedIN    = 25; // LED Đỏ cổng Vào: D25
const int ledGrnIN    = 26; // LED Xanh cổng Vào: D26

// ── Làn Ra (OUT) ──
const int irPinOUT    = 4;  // Cảm biến IR cổng Ra: D4
const int servoPinOUT = 14; // Servo cổng Ra: D14
const int ledRedOUT   = 32; // LED Đỏ cổng Ra: D32
const int ledGrnOUT   = 33; // LED Xanh cổng Ra: D33

// ── Dùng chung ──
const int buzzerPin   = 12; // Còi Buzzer: D12

// ══════════════════════════════════════════════════════════
//  Servo objects
// ══════════════════════════════════════════════════════════
Servo servoIN;
Servo servoOUT;

// ══════════════════════════════════════════════════════════
//  Trạng thái & Biến quản lý mỗi làn
// ══════════════════════════════════════════════════════════
bool gateOpenIN        = false;
bool lastIrStateIN     = false; // Dùng phát hiện cạnh sườn (vừa đưa tay vào)
unsigned long openedAtIN   = 0;
unsigned long lastTriggerIN = 0;

bool gateOpenOUT       = false;
bool lastIrStateOUT    = false; // Dùng phát hiện cạnh sườn
unsigned long openedAtOUT  = 0;
unsigned long lastTriggerOUT = 0;

// ══════════════════════════════════════════════════════════
//  Hằng thời gian
// ══════════════════════════════════════════════════════════
const unsigned long AUTO_CLOSE_MS  = 30000UL; // 30 giây tự đóng
const unsigned long COOLDOWN_MS    =  2500UL; // 2.5 giây chống spam giữa 2 lần quét

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
    openedAtIN = millis(); // Gia hạn thêm 30s
    return;
  }

  gateOpenIN = true;
  openedAtIN = millis();

  // Đèn: Xanh sáng, Đỏ tắt
  digitalWrite(ledRedIN, LOW);
  digitalWrite(ledGrnIN, HIGH);

  // Còi 1 bíp ngắn
  beep(1, 150, 0);

  // Servo quay 90° và giữ nguyên
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

  gateOpenOUT = true;
  openedAtOUT = millis();

  // Đèn: Xanh sáng, Đỏ tắt
  digitalWrite(ledRedOUT, LOW);
  digitalWrite(ledGrnOUT, HIGH);

  // Còi 1 bíp ngắn
  beep(1, 150, 0);

  // Servo quay 90° và giữ nguyên
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
//  SETUP
// ══════════════════════════════════════════════════════════
void setup() {
  Serial.begin(115200);

  // Cảm biến hồng ngoại IR: Mức LOW khi có vật cản
  pinMode(irPinIN,  INPUT_PULLUP);
  pinMode(irPinOUT, INPUT_PULLUP);

  // Đèn LED & Buzzer
  pinMode(ledRedIN,  OUTPUT);
  pinMode(ledGrnIN,  OUTPUT);
  pinMode(ledRedOUT, OUTPUT);
  pinMode(ledGrnOUT, OUTPUT);
  pinMode(buzzerPin, OUTPUT);

  // Trạng thái ban đầu: Cổng đóng, LED Đỏ sáng, LED Xanh tắt
  digitalWrite(ledRedIN,  HIGH);
  digitalWrite(ledGrnIN,  LOW);
  digitalWrite(ledRedOUT, HIGH);
  digitalWrite(ledGrnOUT, LOW);
  digitalWrite(buzzerPin, LOW);

  // Cấp phát Timer cho ESP32PWM (Tránh xung đột 2 servo)
  ESP32PWM::allocateTimer(0);
  ESP32PWM::allocateTimer(1);
  ESP32PWM::allocateTimer(2);
  ESP32PWM::allocateTimer(3);

  servoIN.setPeriodHertz(50);
  servoOUT.setPeriodHertz(50);

  servoIN.attach(servoPinIN,   500, 2400);
  servoOUT.attach(servoPinOUT, 500, 2400);

  servoIN.write(0);
  delay(100);
  servoOUT.write(0);

  Serial.println("BOOT:OK — NexusPort Dual-Lane Gate (IN:D19, OUT:D4)");
}

// ══════════════════════════════════════════════════════════
//  LOOP
// ══════════════════════════════════════════════════════════
void loop() {
  unsigned long now = millis();

  // 1. TỰ ĐỘNG ĐÓNG SAU 30 GIÂY MỖI LÀN
  if (gateOpenIN  && (now - openedAtIN  >= AUTO_CLOSE_MS)) closeGateIN();
  if (gateOpenOUT && (now - openedAtOUT >= AUTO_CLOSE_MS)) closeGateOUT();

  // 2. CẢM BIẾN IR LÀN VÀO (D19) — Dùng Edge Detection: Vừa đưa tay/xe vào là kích hoạt ngay!
  bool isBlockedIN = (digitalRead(irPinIN) == LOW);
  if (isBlockedIN && !lastIrStateIN) {
    if (!gateOpenIN && (now - lastTriggerIN > COOLDOWN_MS)) {
      lastTriggerIN = now;
      Serial.println("[IR_IN:DETECTED] -> Send CAPTURE_IN");
      Serial.println("CAPTURE_IN");
    }
  }
  lastIrStateIN = isBlockedIN;

  // 3. CẢM BIẾN IR LÀN RA (D4) — Dùng Edge Detection
  bool isBlockedOUT = (digitalRead(irPinOUT) == LOW);
  if (isBlockedOUT && !lastIrStateOUT) {
    if (!gateOpenOUT && (now - lastTriggerOUT > COOLDOWN_MS)) {
      lastTriggerOUT = now;
      Serial.println("[IR_OUT:DETECTED] -> Send CAPTURE_OUT");
      Serial.println("CAPTURE_OUT");
    }
  }
  lastIrStateOUT = isBlockedOUT;

  // 4. LẮNG NGHE LỆNH TỪ MÁY TÍNH
  if (Serial.available() > 0) {
    String command = Serial.readStringUntil('\n');
    command.trim();

    // ── Làn Vào ──
    if (command == "OPEN_IN" || command == "OPEN") {
      openGateIN();
    } else if (command == "CLOSE_IN" || command == "CLOSE") {
      closeGateIN();
    } else if (command == "FAIL_IN" || command == "FAIL") {
      beep(2, 100, 100);

    // ── Làn Ra ──
    } else if (command == "OPEN_OUT") {
      openGateOUT();
    } else if (command == "CLOSE_OUT") {
      closeGateOUT();
    } else if (command == "FAIL_OUT") {
      beep(2, 100, 100);

    // ── Ping ──
    } else if (command == "PING") {
      Serial.println("PONG");
    }
  }
}
