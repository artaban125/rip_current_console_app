# 🌊 해안 안전 이안류 및 조난 표류자 실시간 AI 관제 시스템
> **Real-Time Rip Current & Drifter Coastal Safety Monitoring System (.NET 8 / C#)**

[![.NET 8.0](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![YOLO11](https://img.shields.io/badge/AI%20Model-YOLO11m%20%2F%20ONNX-00FFFF)](https://github.com/ultralytics/ultralytics)
[![Platform](https://img.shields.io/badge/Platform-Windows%20x64-0078D6?logo=windows&logoColor=white)](https://www.microsoft.com/)
[![License](https://img.shields.io/badge/License-Proprietary-red)](#)

해안가 고정 CCTV 스트림을 실시간 수신하여 **이안류(Rip Current)** 발생 수역을 검출하고, 해당 위험 수역 내 **표류 조난자(Drifter/Person)**의 시공간 복합 위험도를 실시간으로 추적·경보하는 C# 기반 지능형 해안 안전 통합 관제 솔루션입니다.

---

## 📌 주요 특징 (Key Features)

- **실시간 고화질 영상 처리 파이프라인**: RTSP/CCTV IP 스트림 및 고해상도(1080p 이상) 영상 디코딩 및 실시간 30+ FPS 관제 보장.
- **초경량 하이브리드 추론 (ONNX Runtime / TensorRT)**: 경량 최적화된 YOLO11m 기반의 이안류 및 객체 검출 모델 탑재.
- **이안류-표류자 공간 교차(Spatial Intersection) 판정**:
  - 단순 입수자와 이안류 급류에 휩쓸린 조난자를 정밀 분리 판별.
  - 이안류 경계 박스(BBox/OBB)와 인체 검출 영역 간 공간 기하학적 교집합(IoU/Inclusion) 실시간 연산.
- **시계열 누적 필터링 (Temporal Sequential Verification)**:
  - 파도 거품(Foam) 및 일시적 광학 노이즈에 의한 오탐(False Alarm)을 차단하기 위해 동일 좌표 3~5프레임 이상 연속 검출 시 알람 발령.
- **골든타임 비상 전파**: 위험 감지 즉시 현장 스냅샷 저장 및 관제 대시보드 시각화, 현장 안전요원 전파(REST API, Webhook, Telegram 등).

---

## 🏗 시스템 아키텍처 (Architecture)

```text
[ 해안 CCTV RTSP Stream / Video ]
               │
               ▼
[ VideoCapture & Frame Preprocessing ] ──── (15초 Temporal Stride / Letterboxing)
               │
               ▼
[ ONNX Runtime Inference Engine ]
    ├── 이안류 탐지 (Rip Current BBox / OBB)
    └── 표류자/인체 탐지 (Person Detection)
               │
               ▼
[ Spatial & Temporal Analysis Core ]
    ├── 공간적 포함 관계 판정 (Spatial Intersection: Drifter in Rip Current)
    └── 시계열 3~5프레임 누적 필터 (False Positive Suppression)
               │
               ▼
[ Control Center UI / Emergency Dispatch ]
    ├── 실시간 관제 대시보드 오버레이 (WPF / WinForms / Avalonia)
    ├── 비상 경보 발령 & 스냅샷 아카이빙
    └── 텔레그램 / SMS / 웹소켓 실시간 브로드캐스트
```

---

## 🛠 기술 스택 (Tech Stack)

| 구분 | 기술 / 라이브러리 | 설명 |
| :--- | :--- | :--- |
| **Framework** | .NET 8.0 (C#) | 현대적인 크로스플랫폼/고성능 비동기 런타임 |
| **Inference Engine** | `Microsoft.ML.OnnxRuntime.Gpu` / `DirectML` | CUDA/TensorRT 가속 및 실시간 AI 추론 |
| **Computer Vision** | `OpenCvSharp4`, `OpenCvSharp4.runtime.win` | 영상 캡처, 전처리(Letterbox), 시각 오버레이 |
| **Data Types** | `System.Numerics.Tensors`, `CommunityToolkit.Mvvm` | 고성능 텐서 메모리 조작 및 반응형 관제 UI |
| **Notification** | `System.Net.Http`, `Telegram.Bot` | 비동기 긴급 상황 전파 모듈 |

---

## 📂 프로젝트 디렉터리 구조

```text
RipCurrentMonitoring/
├── 📁 Assets/                  # UI 리소스 및 사운드 효과
├── 📁 Config/                  # 시스템 및 카메라 설정 (appsettings.json)
├── 📁 Models/                  # 데이터 엔티티 및 탐지 결과 DTO
│   ├── DetectionBox.cs         # BBox / OBB 경계 상자 좌표 구조체
│   ├── DrifterAlert.cs         # 위험 경보 이벤트 모델
│   └── CameraFeed.cs           # CCTV 채널 메타데이터
├── 📁 Services/                # 핵심 비즈니스 로직
│   ├── VideoCaptureService.cs  # RTSP/영상 프레임 스트림 공급
│   ├── OnnxInferenceService.cs # ONNX Runtime 기반 YOLO11 추론
│   ├── SpatialAnalysisService.cs# 이안류-조난자 기하학적 교집합 판정
│   ├── TemporalFilter.cs       # 3~5프레임 시계열 누적 검증 로직
│   └── AlertDispatchService.cs # 알림 및 스냅샷 저장 전송
├── 📁 ViewModels/              # MVVM 관제 대시보드 뷰모델
├── 📁 Views/                   # 메인 관제 모니터링 화면
├── 📁 weights/                 # 변환된 ONNX 모델 파일 (.onnx)
├── App.xaml / Program.cs
└── RipCurrentMonitoring.csproj
```

---

## ⚙️ 설정 가이드 (`appsettings.json`)

```json
{
  "DetectionSettings": {
    "ModelPath": "weights/yolo11m_rip_current.onnx",
    "ConfidenceThreshold": 0.25,
    "NmsIouThreshold": 0.60,
    "SequentialFrameThreshold": 3,
    "InputResolution": 1024
  },
  "CameraStreams": [
    {
      "CameraId": "CAM_01_EAST_BEACH",
      "Name": "낙산해수욕장 1번 관제탑",
      "RtspUrl": "rtsp://admin:pass@192.168.1.100:554/stream1",
      "ActiveZone": [0, 0, 1920, 1080]
    }
  ],
  "AlertSettings": {
    "SnapshotPath": "C:/RipAlerts/Snapshots",
    "TelegramBotToken": "YOUR_BOT_TOKEN",
    "TelegramChatId": "YOUR_CHAT_ID"
  }
}
```

---

## 🚀 빌드 및 배포 (Build & Deployment)

본 관제 프로그램은 현장 관제 PC 및 상황실 서버에 **.NET 8 런타임이 사전 설치되어 있지 않아도 즉시 실행 가능한 완전 독립형 단일 파일(Single-File Self-Contained)** 배포를 권장합니다.

### 1. 전제 조건
- Windows 10 / 11 64-bit 또는 Windows Server 2022
- NVIDIA GPU (GeForce RTX 3060 / 4060 이상 권장) 및 CUDA 12.x / cuDNN v9.x (GPU 가속 사용 시)

### 2. 단일 실행 파일(.exe) 원클릭 배포 빌드
네이티브 C/C++ DLL(`OpenCvSharpExtern.dll`, `onnxruntime.dll` 등)까지 실행 파일 하나에 완전히 포함하여 배포합니다:

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./dist
```

> **배포 시 산출물 안내**:
> - `./dist/RipCurrentMonitoring.exe`: 독립 실행형 단일 관제 프로그램.
> - `./dist/weights/`: ONNX AI 모델 가중치 폴더.
> - `./dist/appsettings.json`: 현장 카메라 RTSP 및 임계치 설정 파일.

---

## 🚨 운영 및 장애 대응 (Troubleshooting)

1. **`DllNotFoundException` (OpenCvSharp / ONNXRuntime) 발생 시**:
   - `VC++ 2015-2022 재배포 가능 패키지(x64)`가 설치되어 있는지 확인합니다.
   - 단일 파일 배포 시 `-p:IncludeNativeLibrariesForSelfExtract=true` 옵션이 적용되었는지 확인합니다.
2. **GPU 추론 실패 및 CPU Fallback 현상**:
   - 그래픽 드라이버 버전과 CUDA 런타임 버전이 ONNX Runtime 호환 범위 내에 있는지 확인합니다.
   - GPU 미장착 환경에서는 `Microsoft.ML.OnnxRuntime` (CPU/OpenVINO) 모드로 대체 동작합니다.
3. **일시적 파도 포말 오탐 발생 시**:
   - `appsettings.json`의 `SequentialFrameThreshold`를 `3`에서 `5`로 상향 조정하여 시간적 안정성을 강화합니다.

---

## 📄 라이선스 및 저작권
본 프로젝트는 해안 안전 AI 관제 프로젝트 연구 결과물이며 무단 배포 및 상업적 이용을 금합니다.