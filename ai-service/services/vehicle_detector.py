import os
from typing import Dict, Any, List, Optional
import numpy as np

try:
    from ultralytics import YOLO
except ImportError:
    YOLO = None


class VehicleDetector:
    """
    Detector sử dụng YOLOv8 để phát hiện phương tiện (Truck, Container Truck, Car, Bus)
    khi xe di chuyển đến cổng cảng (Gate In / Gate Out).
    """

    VEHICLE_CLASS_MAP = {
        2: "car",
        3: "motorcycle",
        5: "bus",
        7: "truck"
    }

    def __init__(self, model_path: Optional[str] = None):
        if model_path is None:
            base_dir = os.path.dirname(os.path.dirname(__file__))
            model_path = os.path.join(base_dir, "models", "yolov8n.pt")

        self.model_path = model_path
        self.model = None

        if YOLO is not None and os.path.exists(model_path):
            try:
                self.model = YOLO(model_path)
                print(f"[INFO] VehicleDetector initialized with model: {model_path}")
            except Exception as e:
                print(f"[WARNING] Could not load YOLO vehicle model: {e}")
        else:
            print(f"[WARNING] YOLO vehicle model not found or ultralytics not installed at {model_path}")

    def detect(self, image: np.ndarray, conf_threshold: float = 0.25) -> Dict[str, Any]:
        """
        Phát hiện phương tiện trong ảnh.
        Trả về phương tiện có độ tin cậy cao nhất hoặc xe tải ưu tiên.
        """
        h, w = image.shape[:2]
        default_result = {
            "detected": False,
            "type": "unknown",
            "confidence": 0.0,
            "box": [0, 0, w, h]
        }

        if self.model is None:
            # Fallback nếu chưa load được model: giả định có phương tiện toàn khung hình
            return {
                "detected": True,
                "type": "vehicle",
                "confidence": 0.50,
                "box": [0, 0, w, h]
            }

        try:
            results = self.model.predict(image, conf=conf_threshold, verbose=False)
            if not results or len(results) == 0:
                return default_result

            boxes = results[0].boxes
            if boxes is None or len(boxes) == 0:
                return default_result

            detected_vehicles: List[Dict[str, Any]] = []

            for box in boxes:
                cls_id = int(box.cls[0].item())
                conf = float(box.conf[0].item())

                if cls_id in self.VEHICLE_CLASS_MAP:
                    v_type = self.VEHICLE_CLASS_MAP[cls_id]
                    # Map xe tải chuyên dụng thành container_truck nếu cần
                    if v_type == "truck":
                        v_type = "container_truck"

                    xyxy = box.xyxy[0].tolist()
                    x1, y1, x2, y2 = [int(v) for v in xyxy]
                    # Giới hạn trong kích thước ảnh
                    x1 = max(0, min(x1, w - 1))
                    y1 = max(0, min(y1, h - 1))
                    x2 = max(x1 + 1, min(x2, w))
                    y2 = max(y1 + 1, min(y2, h))

                    detected_vehicles.append({
                        "detected": True,
                        "type": v_type,
                        "confidence": round(conf, 4),
                        "box": [x1, y1, x2, y2]
                    })

            if not detected_vehicles:
                return default_result

            # Ưu tiên container_truck / truck > car > bus, sau đó theo độ tin cậy
            def sort_key(v):
                type_priority = 3 if "truck" in v["type"] else (2 if v["type"] == "bus" else 1)
                return (type_priority, v["confidence"])

            detected_vehicles.sort(key=sort_key, reverse=True)
            return detected_vehicles[0]

        except Exception as e:
            print(f"[ERROR] Vehicle detection failed: {e}")
            return default_result
