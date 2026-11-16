#include <ESP32Servo.h>

const int irPin = 19;       // Cảm biến hồng ngoại
const int servoPin = 13;    // Động cơ Servo (Barrier)
const int buzzerPin = 12;   // Còi Buzzer
const int ledRedPin = 25;   // LED Đỏ
const int ledGreenPin = 26; // LED Xanh

Servo barrierServo;

// Trạng thái Barie & OCR
bool isGateOpen = false;
bool isWaitingForOCR = false;
unsigned long requestTime = 0;
unsigned long gateOpenedAt = 0;

// Thời gian mở barie trước khi tự đóng: đúng 30 giây
const unsigned long AUTO_CLOSE_MS = 30000;

// Cooldown sau FAIL: chống spam còi
unsigned long lastFailTime = 0;
const unsigned long FAIL_COOLDOWN_MS = 8000;

void setup() {
  Serial.begin(115200);

  pinMode(irPin, INPUT_PULLUP);
  pinMode(buzzerPin, OUTPUT);
  pinMode(ledRedPin, OUTPUT);
  pinMode(ledGreenPin, OUTPUT);

  // Gán chân servo với dải xung chuẩn SG90
  barrierServo.attach(servoPin, 500, 2400);

  // Vị trí ban đầu: Đóng cổng (0 độ)
  barrierServo.write(0);
  isGateOpen = false;

  digitalWrite(ledRedPin, HIGH);
  digitalWrite(ledGreenPin, LOW);
  digitalWrite(buzzerPin, LOW);
}

// Hàm mở barie: CHỈ XOAY ĐÚNG 1 LẦN 90 ĐỘ RỒI GIỮ NGUYÊN
void openGate() {
  if (isGateOpen) {
    // Nếu cổng đã mở rồi thì không quay lại, không kêu lại
    gateOpenedAt = millis(); // Gia hạn thêm thời gian mở
    return;
  }

  isGateOpen = true;
  gateOpenedAt = millis(); // Bắt đầu đếm 30s

  // Bật đèn xanh, tắt đèn đỏ
  digitalWrite(ledRedPin, LOW);
  digitalWrite(ledGreenPin, HIGH);

  // Kêu còi 1 tiếng bíp ngắn 200ms
  digitalWrite(buzzerPin, HIGH);
  delay(200);
  digitalWrite(buzzerPin, LOW);

  // Xoay servo lên 90 độ và GIỮ NGUYÊN
  barrierServo.write(90);
  Serial.println("STATUS:OPENED");
}

// Hàm đóng barie: Hạ servo về 0 độ
void closeGate() {
  if (!isGateOpen) {
    // Nếu cổng đã đóng rồi thì không làm gì thêm
    return;
  }

  isGateOpen = false;

  // Xoay servo về 0 độ
  barrierServo.write(0);

  // Đổi đèn: Đỏ sáng (cổng đóng), Xanh tắt
  digitalWrite(ledGreenPin, LOW);
  digitalWrite(ledRedPin, HIGH);
  Serial.println("STATUS:CLOSED");
}

void loop() {
  // 1. TỰ ĐỘNG ĐÓNG SAU ĐÚNG 30 GIÂY MỞ
  if (isGateOpen && (millis() - gateOpenedAt >= AUTO_CLOSE_MS)) {
    closeGate();
  }

  // 2. CẢM BIẾN IR: Khi phát hiện xe (LOW) & Cổng chưa mở & Không đang chờ OCR & Hết cooldown
  bool irTriggered = (digitalRead(irPin) == LOW);
  bool cooldownOk  = (millis() - lastFailTime > FAIL_COOLDOWN_MS);

  if (irTriggered && !isGateOpen && !isWaitingForOCR && cooldownOk) {
    Serial.println("CAPTURE");
    isWaitingForOCR = true;
    requestTime = millis();
    delay(500); // Chống dội tín hiệu
  }

  // 3. LẮNG NGHE LỆNH TỪ MÁY TÍNH GỬI XUỐNG
  if (Serial.available() > 0) {
    String command = Serial.readStringUntil('\n');
    command.trim();

    if (command == "OPEN") {
      openGate();
      isWaitingForOCR = false;

    } else if (command == "CLOSE") {
      closeGate();
      isWaitingForOCR = false;

    } else if (command == "FAIL") {
      // Chỉ kêu 2 tiếng bíp khi xe không hợp lệ (không xoay servo)
      for (int i = 0; i < 2; i++) {
        digitalWrite(buzzerPin, HIGH);
        delay(100);
        digitalWrite(buzzerPin, LOW);
        delay(100);
      }
      isWaitingForOCR = false;
      lastFailTime = millis();
    }
  }

  // 4. Timeout chờ phản hồi OCR (10s)
  if (isWaitingForOCR && (millis() - requestTime > 10000)) {
    isWaitingForOCR = false;
    lastFailTime = millis();
  }
}
