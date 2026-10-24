import os
from typing import Dict, Any, Optional, Tuple
import cv2
import numpy as np


class PlateDetector:
    """
    Detector sử dụng mô hình YOLOv8 ONNX (license_plate.onnx)
    chạy qua OpenCV DNN để phát hiện vùng biển số xe tại cổng cảng.
    """

    def __init__(self, model_path: Optional[str] = None):
        if model_path is None:
            base_dir = os.path.dirname(os.path.dirname(__file__))
            model_path = os.path.join(base_dir, "models", "license_plate.onnx")

        self.model_path = model_path
        self.net = None

        if os.path.exists(model_path):
            try:
                self.net = cv2.dnn.readNetFromONNX(model_path)
                # Tối ưu chạy trên CPU
                self.net.setPreferableBackend(cv2.dnn.DNN_BACKEND_OPENCV)
                self.net.setPreferableTarget(cv2.dnn.DNN_TARGET_CPU)
                print(f"[INFO] PlateDetector loaded ONNX model: {model_path}")
            except Exception as e:
                print(f"[ERROR] Could not load license plate ONNX model: {e}")
        else:
            print(f"[WARNING] license_plate.onnx not found at {model_path}")

    def detect(
        self,
        image: np.ndarray,
        conf_threshold: float = 0.30,
        nms_threshold: float = 0.45
    ) -> Dict[str, Any]:
        """
        Phát hiện tọa độ biển số xe trong ảnh và cắt ảnh vùng biển số.
        """
        h_orig, w_orig = image.shape[:2]
        default_res = {
            "detected": False,
            "confidence": 0.0,
            "box": [0, 0, 0, 0],
            "cropped_plate": None
        }

        if self.net is None:
            return default_res

        try:
            # Chuẩn bị input 640x640 theo chuẩn YOLOv8
            input_size = 640
            blob = cv2.dnn.blobFromImage(
                image,
                scalefactor=1.0 / 255.0,
                size=(input_size, input_size),
                swapRB=True,
                crop=False
            )
            self.net.setInput(blob)
            outputs = self.net.forward()

            # Format YOLOv8: outputs có shape (1, 9, 8400)
            # 0..3: cx, cy, w, h
            # 4..8: class confidences
            preds = outputs[0].transpose((1, 0))  # -> (8400, 9)

            boxes = []
            confidences = []

            scale_x = w_orig / input_size
            scale_y = h_orig / input_size

            for row in preds:
                class_scores = row[4:]
                max_score = float(np.max(class_scores))

                if max_score >= conf_threshold:
                    cx, cy, w, h = row[0:4]
                    x1 = int((cx - w / 2.0) * scale_x)
                    y1 = int((cy - h / 2.0) * scale_y)
                    box_w = int(w * scale_x)
                    box_h = int(h * scale_y)

                    # Đảm bảo box hợp lệ
                    if box_w > 10 and box_h > 10:
                        boxes.append([x1, y1, box_w, box_h])
                        confidences.append(max_score)

            if not boxes:
                return default_res

            # Áp dụng NMS (Non-Maximum Suppression)
            indices = cv2.dnn.NMSBoxes(boxes, confidences, conf_threshold, nms_threshold)
            if len(indices) == 0:
                return default_res

            # Lấy box có độ tin cậy cao nhất
            best_idx = indices[0] if isinstance(indices[0], (int, np.integer)) else indices[0][0]
            x, y, w, h = boxes[best_idx]
            conf = confidences[best_idx]

            # Thêm padding nhẹ xung quanh biển số để tránh cắt sát mép chữ
            pad_x = int(w * 0.05)
            pad_y = int(h * 0.05)

            x1 = max(0, x - pad_x)
            y1 = max(0, y - pad_y)
            x2 = min(w_orig, x + w + pad_x)
            y2 = min(h_orig, y + h + pad_y)

            cropped = image[y1:y2, x1:x2].copy()

            return {
                "detected": True,
                "confidence": round(conf, 4),
                "box": [x1, y1, x2, y2],
                "cropped_plate": cropped
            }

        except Exception as e:
            print(f"[ERROR] Plate detection error: {e}")
            return default_res
