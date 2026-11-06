import os
import base64
import json
import requests
from typing import Optional
from fastapi import FastAPI, UploadFile, File, Form, HTTPException, WebSocket, WebSocketDisconnect
from fastapi.middleware.cors import CORSMiddleware
from pydantic import BaseModel

from services.pipeline import SmartPortGatePipeline

app = FastAPI(
    title="NexusPort AI Smart Gate Service",
    description="Microservice AI nhận diện phương tiện, biển số xe tự động phục vụ Smart Port Gate Automation & IoT Camera Integration",
    version="1.0.0"
)

# CORS cho phép Frontend và Backend .NET kết nối
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

# Khởi tạo Pipeline một lần khi khởi động (Singleton)
pipeline = SmartPortGatePipeline()


def broadcast_to_gate_officers(result: dict, overview_base64: Optional[str] = None):
    """
    Thông báo kết quả nhận diện biển số xe từ IoT Camera tới Backend Node.js
    để phát Realtime qua Socket.IO tới màn hình Cổng kiểm soát (Gate Officer).
    """
    try:
        plate_obj = result.get("license_plate", {})
        plate = plate_obj.get("plate_number")
        if not plate:
            return

        conf = plate_obj.get("ocr_confidence") or 0.98
        crop_img = plate_obj.get("plate_image_base64")

        payload = {
            "gateCode": result.get("gate_code", "GATE_IN_A"),
            "laneCode": result.get("lane_code", "LANE_01"),
            "cameraId": result.get("camera_id", "CAM_GATE_01"),
            "detectedPlate": plate,
            "plateConfidence": conf,
            "vehicleDetected": True,
            "vehicleType": result.get("vehicle", {}).get("type", "container_truck"),
            "vehiclePlateImageUrl": f"data:image/jpeg;base64,{crop_img}" if crop_img and not crop_img.startswith("data:") else crop_img,
            "overviewImageUrl": f"data:image/jpeg;base64,{overview_base64}" if overview_base64 and not overview_base64.startswith("data:") else overview_base64,
            "timestamp": result.get("recognized_at"),
            "source": "IoT_Camera"
        }

        # Gọi Node.js Express (:3001) để emit Socket.IO
        requests.post("http://localhost:3001/api/gate/iot-event", json=payload, timeout=1.5)
    except Exception as e:
        print(f"[WARN] broadcast_to_gate_officers failed: {e}")


class IotScanJsonRequest(BaseModel):
    image_base64: str
    camera_id: Optional[str] = "CAM_GATE_01"
    gate_code: Optional[str] = "GATE_IN_A"
    lane_code: Optional[str] = "LANE_01"


class SimulateRequest(BaseModel):
    vehicle_plate: str = "51C-123.45"
    vehicle_type: str = "container_truck"
    camera_id: str = "CAM_GATE_01"
    gate_code: str = "GATE_IN_A"
    lane_code: str = "LANE_01"


@app.get("/health")
def health_check():
    """
    Kiểm tra tình trạng sức khỏe của AI Service và trạng thái các model.
    """
    base_dir = os.path.dirname(__file__)
    plate_model_exists = os.path.exists(os.path.join(base_dir, "models", "license_plate.onnx"))
    vehicle_model_exists = os.path.exists(os.path.join(base_dir, "models", "yolov8n.pt"))

    return {
        "status": "healthy" if pipeline.ocr_service.azure_available else "degraded",
        "recognition_ready": pipeline.ocr_service.azure_available,
        "message": "OCR đã được cấu hình; kết nối Azure được kiểm tra khi nhận diện ảnh." if pipeline.ocr_service.azure_available
          else "Chưa cấu hình Azure OCR. Kiểm tra AZURE_VISION_ENDPOINT và AZURE_VISION_KEY trong ai-service/.env hoặc .env của Backend.",
        "service": "NexusPort AI Smart Gate",
        "models": {
            "license_plate_onnx": plate_model_exists,
            "azure_vision_ocr": pipeline.ocr_service.azure_available
        }
    }


@app.post("/api/v1/gate/recognize")
async def recognize_vehicle_at_gate(
    file: UploadFile = File(..., description="Ảnh chụp phương tiện từ AI Camera tại cổng"),
    camera_id: str = Form("CAM_GATE_01", description="Mã định danh của Camera cổng"),
    gate_code: str = Form("GATE_IN_A", description="Mã cổng (Gate In / Gate Out)"),
    lane_code: str = Form("LANE_01", description="Mã làn xe")
):
    """
    ### Luồng xử lý tự động khi xe đến cổng (Role: Driver + AI Camera):
    1. **Camera nhận được hình ảnh** (Decode image)
    2. **Lưu thời gian nhận diện** (Timestamp UTC ISO-8601)
    3. **YOLO phát hiện phương tiện** (Detect vehicle type: container_truck, truck, car)
    4. **Phát hiện vùng biển số** (Detect license plate bounding box)
    5. **OCR đọc biển số** (Nhận diện ký tự biển số xe Việt Nam)
    6. **Trả về Recognition Result** (chuẩn hóa tương thích Entity GateVerificationRecord của C# .NET)
    7. **Tự động broadcast Realtime** tới màn hình Gate Officer
    """
    try:
        if not pipeline.ocr_service.azure_available:
            raise HTTPException(status_code=503, detail="OCR chưa được cấu hình. Kiểm tra AZURE_VISION_ENDPOINT và AZURE_VISION_KEY rồi khởi động lại AI service.")
        contents = await file.read()
        if not contents:
            raise HTTPException(status_code=400, detail="File ảnh rỗng.")

        result = pipeline.process_image(
            image_bytes=contents,
            camera_id=camera_id,
            lane_code=lane_code,
            gate_code=gate_code
        )

        # Broadcast realtime tới nhân viên cổng nếu nhận diện thấy biển số
        overview_b64 = base64.b64encode(contents).decode("utf-8")
        broadcast_to_gate_officers(result, overview_b64)

        return result
    except HTTPException:
        raise
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"Lỗi xử lý nhận diện: {str(e)}")


@app.post("/api/v1/gate/iot-scan")
def iot_scan_endpoint(req: IotScanJsonRequest):
    """
    API dành riêng cho thiết bị IoT (ESP32-CAM, Vi điều khiển, Raspberry Pi) gửi ảnh Base64
    không cần format multipart/form-data.
    """
    try:
        raw_b64 = req.image_base64
        if "," in raw_b64:
            raw_b64 = raw_b64.split(",")[1]
        image_bytes = base64.b64decode(raw_b64)

        result = pipeline.process_image(
            image_bytes=image_bytes,
            camera_id=req.camera_id or "CAM_GATE_01",
            lane_code=req.lane_code or "LANE_01",
            gate_code=req.gate_code or "GATE_IN_A"
        )

        # Tự động gửi kết quả lên màn hình Gate Control
        broadcast_to_gate_officers(result, raw_b64)

        return result
    except Exception as e:
        raise HTTPException(status_code=400, detail=f"Lỗi xử lý ảnh từ thiết bị IoT: {str(e)}")


@app.websocket("/ws/gate/camera")
async def websocket_gate_camera(
    websocket: WebSocket,
    camera_id: str = "CAM_GATE_01",
    gate_code: str = "GATE_IN_A",
    lane_code: str = "LANE_01"
):
    """
    WebSocket endpoint chuyên dụng cho IoT (ESP32-CAM / Raspberry Pi).
    Thiết bị IoT kết nối WebSocket và đẩy dữ liệu liên tục:
      - Binary frame: Ảnh JPEG nhị phân trực tiếp từ camera
      - Hoặc Text JSON: {"image": "base64...", "camera_id": "..."}
    Mỗi khi nhận diện được biển số xe hợp lệ, AI Service sẽ:
      1. Trả ngay phản hồi JSON qua WebSocket cho IoT (để bật đèn LED / hiển thị LCD)
      2. Phát Realtime lên màn hình nhân viên cổng (Gate Control)
    """
    await websocket.accept()
    print(f"[INFO] [WebSocket] IoT Camera connected: {camera_id} at {gate_code}/{lane_code}")

    try:
        while True:
            # Nhận dữ liệu (có thể là binary JPEG từ ESP32-CAM hoặc text JSON)
            message = await websocket.receive()
            image_bytes = None
            overview_b64 = None

            if "bytes" in message and message["bytes"]:
                image_bytes = message["bytes"]
                overview_b64 = base64.b64encode(image_bytes).decode("utf-8")
            elif "text" in message and message["text"]:
                try:
                    data = json.loads(message["text"])
                    b64_str = data.get("image") or data.get("image_base64")
                    if b64_str:
                        if "," in b64_str:
                            b64_str = b64_str.split(",")[1]
                        image_bytes = base64.b64decode(b64_str)
                        overview_b64 = b64_str
                        camera_id = data.get("camera_id", camera_id)
                        gate_code = data.get("gate_code", gate_code)
                        lane_code = data.get("lane_code", lane_code)
                except Exception as parse_err:
                    await websocket.send_json({"error": "INVALID_JSON", "detail": str(parse_err)})
                    continue

            if not image_bytes:
                await websocket.send_json({"error": "NO_IMAGE_DATA"})
                continue

            # Xử lý nhận diện qua AI Pipeline
            result = pipeline.process_image(
                image_bytes=image_bytes,
                camera_id=camera_id,
                lane_code=lane_code,
                gate_code=gate_code
            )

            plate_info = result.get("license_plate", {})
            has_plate = bool(plate_info.get("detected") and plate_info.get("plate_number"))

            if has_plate:
                # Phát Realtime lên web quản trị cổng
                broadcast_to_gate_officers(result, overview_b64)

            # Trả phản hồi ngay về cho thiết bị IoT
            await websocket.send_json({
                "status": "success",
                "plate_detected": has_plate,
                "plate_number": plate_info.get("plate_number", ""),
                "confidence": plate_info.get("ocr_confidence", 0),
                "recognized_at": result.get("recognized_at")
            })

    except WebSocketDisconnect:
        print(f"[INFO] [WebSocket] IoT Camera disconnected: {camera_id}")
    except Exception as e:
        print(f"[ERROR] [WebSocket] IoT stream error: {e}")
        try:
            await websocket.close()
        except Exception:
            pass


@app.post("/api/v1/gate/simulate")
def simulate_gate_entry(request: SimulateRequest):
    """
    Giả lập xe container đến cổng để kiểm thử luồng FE & Backend C#
    mà không cần camera vật lý.
    """
    import uuid
    from datetime import datetime, timezone

    recognized_at = datetime.now(timezone.utc).isoformat()
    return {
        "success": True,
        "recognition_id": str(uuid.uuid4()),
        "camera_id": request.camera_id,
        "gate_code": request.gate_code,
        "lane_code": request.lane_code,
        "recognized_at": recognized_at,
        "processing_time_ms": 95.0,
        "status": "PASS",
        "vehicle": {
            "detected": True,
            "type": request.vehicle_type,
            "confidence": 0.96,
            "box": [100, 150, 900, 800]
        },
        "license_plate": {
            "detected": True,
            "plate_number": request.vehicle_plate,
            "confidence": 0.95,
            "box": [450, 600, 650, 670],
            "plate_image_base64": None
        },
        "raw_ocr_data": request.vehicle_plate,
        "gate_module_payload": {
            "VerificationType": "AI_GATE_IN",
            "VerificationStatus": "PASS",
            "VerificationTime": recognized_at,
            "DetectedPlate": request.vehicle_plate,
            "PlateConfidence": 0.95,
            "VehicleDetected": True,
            "CameraId": request.camera_id,
            "GateCode": request.gate_code,
            "LaneCode": request.lane_code,
            "OcrRawData": request.vehicle_plate,
            "VehiclePlateImageUrl": None
        }
    }
