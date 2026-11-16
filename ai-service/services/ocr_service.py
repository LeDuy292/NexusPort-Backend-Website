import re
import os
import cv2
import numpy as np
import requests as http_requests
from typing import Dict, Any, List, Optional, Tuple

from dotenv import load_dotenv

# Explicit process settings > AI-local settings > shared backend settings.
# Never depend on the terminal's current directory or copy credentials into code.
service_dir = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
load_dotenv(os.path.join(service_dir, ".env"), override=False)
load_dotenv(os.path.join(os.path.dirname(service_dir), ".env"), override=False)

# Azure AI Vision credentials từ .env
AZURE_ENDPOINT = os.getenv("AZURE_VISION_ENDPOINT", "").strip().rstrip("/")
AZURE_KEY = os.getenv("AZURE_VISION_KEY", "").strip()
AZURE_REGION = os.getenv("AZURE_VISION_REGION", "eastus").strip()


class OCRService:
    """
    Dịch vụ OCR nhận diện ký tự biển số xe Việt Nam.
    - Ưu tiên 1: Azure AI Vision Read API (~0.5-1.0s, chính xác cao nhất)
    - Fallback  : EasyOCR local (chạy offline, không cần cloud credentials)
    """

    def __init__(self, languages: List[str] = ["en"], gpu: bool = False):
        self.endpoint = AZURE_ENDPOINT
        self.key = AZURE_KEY
        self.azure_available = bool(self.endpoint and self.key)

        # EasyOCR lazy-load (tải model lần đầu mất ~10-20s)
        self._easyocr_reader = None
        self._easyocr_languages = languages
        self._easyocr_gpu = gpu

        if self.azure_available:
            print(f"[INFO] OCRService: Azure AI Vision active at {self.endpoint}")
        else:
            print("[WARN] OCRService: Azure Vision credentials missing — sẽ dùng EasyOCR local làm fallback.")
            self._load_easyocr()

    def _load_easyocr(self):
        """Tải EasyOCR reader (singleton, chỉ load 1 lần)."""
        if self._easyocr_reader is not None:
            return
        try:
            import easyocr
            print("[INFO] OCRService: Đang tải EasyOCR model (lần đầu có thể mất ~20s)...")
            self._easyocr_reader = easyocr.Reader(self._easyocr_languages, gpu=self._easyocr_gpu)
            print("[INFO] OCRService: EasyOCR sẵn sàng.")
        except ImportError:
            print("[ERROR] OCRService: easyocr chưa được cài. Chạy: pip install easyocr")
        except Exception as e:
            print(f"[ERROR] OCRService: Không tải được EasyOCR: {e}")

    def extract_plate_from_lines(self, lines: List[str]) -> Tuple[str, float]:
        """
        Trích xuất biển số xe Việt Nam từ danh sách các dòng chữ nhận diện được.
        Hỗ trợ:
        - Biển xe máy 2 hàng: '29-Y3', '036.58' -> '29Y3-036.58'
        - Biển ô tô 5 số: '51C-123.45', '51C', '123.45' -> '51C-123.45'
        - Biển ô tô 4 số: '29A-1234' -> '29A-1234'
        """
        if not lines:
            return "", 0.0

        candidates = []
        for line in lines:
            cleaned_line = line.strip()
            if cleaned_line:
                candidates.append(cleaned_line)

        # Ghép 2 dòng liên tiếp (dành cho biển 2 hàng)
        for i in range(len(lines) - 1):
            candidates.append(f"{lines[i].strip()} {lines[i+1].strip()}")

        # Ghép toàn bộ chuỗi
        candidates.append(" ".join(lines))

        # Regex patterns biển số VN
        # 1. Xe máy 2 hàng (2 số tỉnh + 1 chữ cái + 1 số seri + 5 số thứ tự): 29-Y3 036.58 -> 29Y3-036.58
        p_motorcycle = re.compile(r"([0-9]{2})[\s\-._]*([A-Z][0-9])[\s\-._]*([0-9]{3})[\s\-._]*([0-9]{2})")
        # 2. Ô tô 5 số (2 số tỉnh + 1-2 chữ cái + 5 số): 51C-123.45, 59A 123.45, 51AB-123.45
        p_car5 = re.compile(r"([0-9]{2})[\s\-._]*([A-Z]{1,2})[\s\-._]*([0-9]{3})[\s\-._]*([0-9]{2})")
        # 3. Ô tô 4 số: 29A-1234
        p_car4 = re.compile(r"([0-9]{2})[\s\-._]*([A-Z]{1,2})[\s\-._]*([0-9]{4})")

        for text in candidates:
            text_clean = text.upper()

            # Thử xe máy trước (mẫu cụ thể hơn)
            m = p_motorcycle.search(text_clean)
            if m:
                prov, ser, num1, num2 = m.groups()
                return f"{prov}{ser}-{num1}.{num2}", 0.95

            # Thử ô tô 5 số
            m = p_car5.search(text_clean)
            if m:
                prov, ser, num1, num2 = m.groups()
                return f"{prov}{ser}-{num1}.{num2}", 0.95

            # Thử ô tô 4 số
            m = p_car4.search(text_clean)
            if m:
                prov, ser, num = m.groups()
                return f"{prov}{ser}-{num}", 0.90

        # Fallback: loại bỏ ký tự lạ và kiểm tra nếu có chuỗi 7-9 ký tự
        for text in candidates:
            cleaned = re.sub(r"[^A-Z0-9]", "", text.upper())
            m_gen = re.match(r"^([0-9]{2}[A-Z]{1,2}[0-9]?)([0-9]{4,5})$", cleaned)
            if m_gen:
                head, tail = m_gen.group(1), m_gen.group(2)
                if len(tail) == 5:
                    return f"{head}-{tail[:3]}.{tail[3:]}", 0.85
                return f"{head}-{tail}", 0.85

        return "", 0.0

    def _preprocess_plate(self, img: np.ndarray) -> np.ndarray:
        """
        Tiền xử lý ảnh biển số để tăng độ chính xác EasyOCR:
        - Resize lên 2x (biển số nhỏ hay bị mờ)
        - Chuyển Grayscale → tăng tương phản CLAHE
        - Sharpen nhẹ
        """
        h, w = img.shape[:2]
        # Upscale nếu ảnh quá nhỏ
        if w < 300:
            scale = 300 / w
            img = cv2.resize(img, (int(w * scale), int(h * scale)), interpolation=cv2.INTER_CUBIC)

        gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)
        clahe = cv2.createCLAHE(clipLimit=2.5, tileGridSize=(4, 4))
        gray = clahe.apply(gray)
        # Sharpen
        kernel = np.array([[0, -1, 0], [-1, 5, -1], [0, -1, 0]])
        gray = cv2.filter2D(gray, -1, kernel)
        # Trả về BGR để EasyOCR nhận
        return cv2.cvtColor(gray, cv2.COLOR_GRAY2BGR)

    def _recognize_easyocr(self, plate_img: np.ndarray) -> Dict[str, Any]:
        """
        Nhận diện biển số qua EasyOCR (chạy local, không cần cloud).
        Dùng làm fallback khi Azure không khả dụng.
        """
        default_res = {"plate_number": "", "confidence": 0.0, "raw_text": "", "raw_details": []}

        if self._easyocr_reader is None:
            self._load_easyocr()
        if self._easyocr_reader is None:
            return default_res

        try:
            processed = self._preprocess_plate(plate_img)
            results = self._easyocr_reader.readtext(processed, detail=1, paragraph=False)

            all_lines = []
            all_words = []
            for (_, text, prob) in results:
                text = text.strip()
                if text:
                    all_lines.append(text)
                    all_words.append({"text": text, "confidence": prob})

            raw_full = " ".join(all_lines)
            avg_conf = float(sum(w["confidence"] for w in all_words) / len(all_words)) if all_words else 0.0

            print(f"[INFO] EasyOCR raw lines: {all_lines}")

            formatted_plate, format_conf = self.extract_plate_from_lines(all_lines)
            final_conf = round(avg_conf * (format_conf if format_conf > 0 else 0.8), 4)

            if formatted_plate:
                print(f"[INFO] EasyOCR success: '{formatted_plate}' (conf: {final_conf}) raw: '{raw_full}'")
            else:
                print(f"[INFO] EasyOCR: không khớp pattern biển số VN từ: {all_lines}")

            return {
                "plate_number": formatted_plate,
                "confidence": final_conf if formatted_plate else 0.0,
                "raw_text": raw_full,
                "raw_details": all_words
            }
        except Exception as e:
            print(f"[ERROR] EasyOCR exception: {e}")
            return default_res

    def recognize(self, plate_img: np.ndarray) -> Dict[str, Any]:
        """
        Nhận diện biển số xe:
        - Ưu tiên: Azure AI Vision Read API (cloud, chính xác cao)
        - Fallback: EasyOCR local (không cần internet/credentials)
        """
        default_res = {
            "plate_number": "",
            "confidence": 0.0,
            "raw_text": "",
            "raw_details": []
        }

        if plate_img is None or plate_img.size == 0:
            return default_res

        # ── Nếu không có Azure → dùng EasyOCR ngay ──────────────────────────
        if not self.azure_available:
            return self._recognize_easyocr(plate_img)

        # ── Thử Azure trước ───────────────────────────────────────────────────
        try:
            success, buf = cv2.imencode(".jpg", plate_img, [int(cv2.IMWRITE_JPEG_QUALITY), 95])
            if not success:
                return default_res
            image_bytes = buf.tobytes()

            url = f"{self.endpoint}/computervision/imageanalysis:analyze"
            params = {
                "api-version": "2024-02-01",
                "features": "read",
                "language": "en"
            }
            headers = {
                "Ocp-Apim-Subscription-Key": self.key,
                "Content-Type": "application/octet-stream"
            }

            resp = http_requests.post(url, params=params, headers=headers,
                                      data=image_bytes, timeout=10)

            if resp.status_code != 200:
                print(f"[WARNING] Azure OCR HTTP {resp.status_code}: {resp.text[:200]}")
                # Fallback sang EasyOCR khi Azure lỗi
                print("[INFO] Fallback sang EasyOCR...")
                return self._recognize_easyocr(plate_img)

            data = resp.json()
            read_result = data.get("readResult", {})
            blocks = read_result.get("blocks", [])

            all_lines = []
            all_words = []
            for block in blocks:
                for line in block.get("lines", []):
                    line_text = line.get("text", "").strip()
                    if line_text:
                        all_lines.append(line_text)
                        for word in line.get("words", []):
                            all_words.append({
                                "text": word.get("text", ""),
                                "confidence": word.get("confidence", 0.0)
                            })

            raw_full = " ".join(all_lines)
            avg_word_conf = float(sum(w["confidence"] for w in all_words) / len(all_words)) if all_words else 0.8

            formatted_plate, format_conf = self.extract_plate_from_lines(all_lines)
            final_conf = round(avg_word_conf * (format_conf if format_conf > 0 else 0.8), 4)

            if formatted_plate:
                print(f"[INFO] Azure OCR success: '{formatted_plate}' (conf: {final_conf}) from raw: '{raw_full}'")
            else:
                print(f"[INFO] Azure OCR lines: {all_lines} (no valid VN plate matched)")
                # Fallback EasyOCR nếu Azure không match pattern
                if all_lines:
                    print("[INFO] Azure không khớp pattern — thử EasyOCR fallback...")
                    easy_res = self._recognize_easyocr(plate_img)
                    if easy_res["plate_number"]:
                        return easy_res

            return {
                "plate_number": formatted_plate,
                "confidence": final_conf if formatted_plate else 0.0,
                "raw_text": raw_full,
                "raw_details": all_words
            }

        except Exception as e:
            print(f"[ERROR] Azure OCR exception: {e} — fallback EasyOCR")
            return self._recognize_easyocr(plate_img)
