using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using OpenCvSharp.Dnn;

namespace RipCurrentConsole
{
    public class DetectionBox
    {
        public Rect2f Box { get; set; }
        public float Confidence { get; set; }
        public int ClassId { get; set; }
    }

    public class YoloDetector : IDisposable
    {
        private readonly InferenceSession _session;
        private readonly int _inputWidth;
        private readonly int _inputHeight;
        private readonly string _inputName;

        public YoloDetector(string modelPath, int fallbackWidth = 640, int fallbackHeight = 640)
        {
            var options = new SessionOptions
            {
                IntraOpNumThreads = Environment.ProcessorCount,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
            };

            // ⭐️ 작업 관리자 확인 결과 RTX 4060 = GPU 0번
            try
            {
                // GPU 0번에 DirectML 연결
                options.AppendExecutionProvider_DML(0);
                System.Diagnostics.Debug.WriteLine($"[성공] RTX 4060 (GPU 0) DirectML 가속 활성화: {Path.GetFileName(modelPath)}");
            }
            catch (Exception ex)
            {
                // ⭐️ 만약 실패한다면 이유를 숨기지 않고 팝업으로 표시
                System.Windows.MessageBox.Show(
                    $"[GPU 가속 활성화 실패]\n모델: {Path.GetFileName(modelPath)}\n원인: {ex.Message}\n\nCPU 모드로 동작하므로 느려질 수 있습니다.",
                    "DirectML 알림",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
            }

            _session = new InferenceSession(modelPath, options);

            // 1. 모델 입력 노드명 획득
            _inputName = _session.InputMetadata.Keys.First();
            var inputMeta = _session.InputMetadata[_inputName];

            // 2. 모델 내부 입력 형상 자동 감지
            int modelH = (inputMeta.Dimensions.Length >= 4 && inputMeta.Dimensions[2] > 0)
                ? inputMeta.Dimensions[2]
                : fallbackHeight;
            int modelW = (inputMeta.Dimensions.Length >= 4 && inputMeta.Dimensions[3] > 0)
                ? inputMeta.Dimensions[3]
                : fallbackWidth;

            _inputWidth = modelW;
            _inputHeight = modelH;
        }

        public List<DetectionBox> Detect(Mat origImage, float confThreshold = 0.05f, float iouThreshold = 0.45f)
        {
            int origW = origImage.Width;
            int origH = origImage.Height;

            // 1. C++ 네이티브 고속 전처리 (BGR -> RGB, 1/255 정규화, 1x3xHxW Blob 변환을 C++ 레벨에서 한 번에 처리)
            using var blob = CvDnn.BlobFromImage(
                origImage,
                scaleFactor: 1.0 / 255.0,
                size: new OpenCvSharp.Size(_inputWidth, _inputHeight),
                mean: new Scalar(0, 0, 0),
                swapRB: true,
                crop: false
            );

            // 2. 텐서 래핑
            float[] tensorData = new float[1 * 3 * _inputHeight * _inputWidth];
            System.Runtime.InteropServices.Marshal.Copy(blob.Data, tensorData, 0, tensorData.Length);
            var inputTensor = new DenseTensor<float>(tensorData, new[] { 1, 3, _inputHeight, _inputWidth });

            // 3. ONNX 추론 실행
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(_inputName, inputTensor)
            };

            using var results = _session.Run(inputs);
            var outputTensor = results.First().AsTensor<float>();

            // 4. 고속 후처리 파싱 (조기 탈출 알고리즘 적용)
            var dims = outputTensor.Dimensions;
            int channels = dims[1]; // 4 (cx, cy, w, h) + class count
            int anchors = dims[2];  // 8400 등

            float scaleX = (float)origW / _inputWidth;
            float scaleY = (float)origH / _inputHeight;

            var boxes = new List<Rect2d>();
            var confidences = new List<float>();
            var classIds = new List<int>();

            for (int i = 0; i < anchors; i++)
            {
                // YOLO11 포맷: 0~3은 cx, cy, w, h / 4부터 각 클래스의 점수
                float maxScore = outputTensor[0, 4, i];
                int maxClassId = 0;

                // 클래스가 2개 이상일 때만 추가 루프 수행
                if (channels > 5)
                {
                    for (int c = 5; c < channels; c++)
                    {
                        float score = outputTensor[0, c, i];
                        if (score > maxScore)
                        {
                            maxScore = score;
                            maxClassId = c - 4;
                        }
                    }
                }

                // [조기 탈출] 임계값 미만인 앵커는 좌표 계산 및 객체 생성을 건너뛰어 CPU 부하 절감
                if (maxScore < confThreshold)
                {
                    continue;
                }

                float cx = outputTensor[0, 0, i] * scaleX;
                float cy = outputTensor[0, 1, i] * scaleY;
                float w = outputTensor[0, 2, i] * scaleX;
                float h = outputTensor[0, 3, i] * scaleY;

                float x1 = Math.Max(0, cx - w / 2f);
                float y1 = Math.Max(0, cy - h / 2f);

                boxes.Add(new Rect2d(x1, y1, w, h));
                confidences.Add(maxScore);
                classIds.Add(maxClassId);
            }

            // 5. NMS(비최대 억제)를 통한 중복 바운딩 박스 제거
            var finalDetections = new List<DetectionBox>();
            if (boxes.Count > 0)
            {
                CvDnn.NMSBoxes(boxes, confidences, confThreshold, iouThreshold, out int[] indices);
                foreach (var idx in indices)
                {
                    finalDetections.Add(new DetectionBox
                    {
                        Box = new Rect2f((float)boxes[idx].X, (float)boxes[idx].Y, (float)boxes[idx].Width, (float)boxes[idx].Height),
                        Confidence = confidences[idx],
                        ClassId = classIds[idx]
                    });
                }
            }

            return finalDetections;
        }

        public void Dispose()
        {
            _session?.Dispose();
        }
    }
}