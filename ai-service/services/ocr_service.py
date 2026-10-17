import re
import os
import cv2
import numpy as np
import requests as http_requests
from typing import Dict, Any, List, Optional, Tuple

try:
    from dotenv import load_dotenv
    env_path = os.path.join(os.path.dirname(os.path.dirname(__file__)), ".env")
    if os.path.exists(env_path):
        load_dotenv(env_path)
except ImportError:
    pass

# Azure AI Vision credentials từ .env
AZURE_ENDPOINT = os.getenv("AZURE_VISION_ENDPOINT", "").strip().rstrip("/")
AZURE_KEY = os.getenv("AZURE_VISION_KEY", "").strip()
AZURE_REGION = os.getenv("AZURE_VISION_REGION", "eastus").strip()


class OCRService:
    """
    Dịch vụ OCR nhận diện ký tự biển số xe Việt Nam sử dụng Azure AI Vision Read API.
    - Tốc độ xử lý: ~0.5 - 1.0 giây
    - Nhận diện chính xác biển xe ô tô, xe tải, container và biển xe máy 2 hàng.
    """

    def __init__(self, languages: List[str] = ["en"], gpu: bool = False):
        self.endpoint = AZURE_ENDPOINT
        self.key = AZURE_KEY
        self.azure_available = bool(self.endpoint and self.key)

        if self.azure_available:
            print(f"[INFO] OCRService: Azure AI Vision active at {self.endpoint}")
        else:
            print("[ERROR] OCRService: Azure Vision credentials missing!")

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

    def recognize(self, plate_img: np.ndarray) -> Dict[str, Any]:
        """
        Nhận diện biển số xe qua Azure AI Vision Read API.
        """
        default_res = {
            "plate_number": "",
            "confidence": 0.0,
            "raw_text": "",
            "raw_details": []
        }

        if plate_img is None or plate_img.size == 0:
            return default_res

        if not self.azure_available:
            print("[ERROR] Azure AI Vision not configured.")
            return default_res

        try:
            # Mã hóa ảnh sang JPEG chất lượng cao
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
                return default_res

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

            # Trích xuất biển số chuẩn từ danh sách các dòng Azure đọc được
            formatted_plate, format_conf = self.extract_plate_from_lines(all_lines)
            final_conf = round(avg_word_conf * (format_conf if format_conf > 0 else 0.8), 4)

            if formatted_plate:
                print(f"[INFO] Azure OCR success: '{formatted_plate}' (conf: {final_conf}) from raw: '{raw_full}'")
            else:
                print(f"[INFO] Azure OCR lines: {all_lines} (no valid VN plate matched)")

            return {
                "plate_number": formatted_plate,
                "confidence": final_conf if formatted_plate else 0.0,
                "raw_text": raw_full,
                "raw_details": all_words
            }

        except Exception as e:
            print(f"[ERROR] Azure OCR exception: {e}")
            return default_res
