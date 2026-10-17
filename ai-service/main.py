import os
from typing import Optional
from fastapi import FastAPI, UploadFile, File, Form, HTTPException
from fastapi.middleware.cors import CORSMiddleware
from pydantic import BaseModel

from services.pipeline import SmartPortGatePipeline

app = FastAPI(
    title="NexusPort AI Smart Gate Service",
    description="Microservice AI nhận diện phương tiện, biển số xe tự động phục vụ Smart Port Gate Automation",
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
        "status": "healthy",
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
    """
    try:
        contents = await file.read()
        if not contents:
            raise HTTPException(status_code=400, detail="File ảnh rỗng.")

        result = pipeline.process_image(
            image_bytes=contents,
            camera_id=camera_id,
            lane_code=lane_code,
            gate_code=gate_code
        )

        return result
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"Lỗi xử lý nhận diện: {str(e)}")


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
