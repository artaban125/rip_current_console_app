using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;

namespace RipCurrentConsole
{
    public static class AlertDispatcher
    {
        private const string TOPIC = "rip_current_alert";
        private const int SERVER_PORT = 8501;

        // 노트북 실제 Wi-Fi IP 설정
        private const string LAPTOP_IP = "192.168.0.34";
        private static readonly string BaseUrl = $"http://{LAPTOP_IP}:{SERVER_PORT}";

        private static HttpListener? _httpListener;
        private static CancellationTokenSource? _serverCts;
        private static bool _isFirebaseInitialized = false;
        private static readonly object LockObj = new object();

        // 1. 내장 정적 웹서버 시작 (로컬 및 Wi-Fi 외부 IP 바인딩)
        public static void StartEmbeddedWebServer(string snapshotDir)
        {
            if (_httpListener != null && _httpListener.IsListening) return;

            try
            {
                _serverCts = new CancellationTokenSource();
                _httpListener = new HttpListener();

                // 로컬 및 스마트폰 Wi-Fi 접속용 바인딩 접두사 등록
                _httpListener.Prefixes.Add($"http://127.0.0.1:{SERVER_PORT}/");
                _httpListener.Prefixes.Add($"http://localhost:{SERVER_PORT}/");
                _httpListener.Prefixes.Add($"http://{LAPTOP_IP}:{SERVER_PORT}/");
                _httpListener.Start();

                Task.Run(() => ServerLoop(snapshotDir, _serverCts.Token));
                System.Diagnostics.Debug.WriteLine($"[웹서버 시작] {BaseUrl} 에서 스냅샷 서빙 대기 중");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[웹서버 시작 실패]: {ex.Message}");
            }
        }

        // 2. 비동기 HTTP 요청 수신 루프 (종료 예외 안전 처리)
        private static async Task ServerLoop(string snapshotDir, CancellationToken token)
        {
            while (!token.IsCancellationRequested && _httpListener != null && _httpListener.IsListening)
            {
                try
                {
                    var context = await _httpListener.GetContextAsync();
                    _ = Task.Run(() => HandleRequest(context, snapshotDir));
                }
                catch (HttpListenerException)
                {
                    // 서버 Stop/Close 시 발생하는 소켓 중단 예외 -> 정상 루프 탈출
                    break;
                }
                catch (ObjectDisposedException)
                {
                    // 리스너가 이미 Dispose된 경우 -> 정상 루프 탈출
                    break;
                }
                catch (InvalidOperationException)
                {
                    // 리스너 상태 변경 중단 -> 정상 루프 탈출
                    break;
                }
                catch when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[웹서버 루프 예외]: {ex.Message}");
                }
            }
        }

        // 3. HTTP 요청 처리기 (안드로이드 이미지 다운로드 맞춤)
        private static void HandleRequest(HttpListenerContext context, string snapshotDir)
        {
            try
            {
                string rawPath = context.Request.Url?.AbsolutePath ?? "";
                string decodedPath = Uri.UnescapeDataString(rawPath);
                string fileName = Path.GetFileName(decodedPath);
                string filePath = Path.Combine(snapshotDir, fileName);

                System.Diagnostics.Debug.WriteLine($"[웹서버 요청]: {rawPath} -> 파일: {filePath}");

                if (!string.IsNullOrEmpty(fileName) && File.Exists(filePath))
                {
                    // 파일 쓰기 락 충돌 방지: FileShare.ReadWrite 모드 사용
                    byte[] bytes;
                    using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var ms = new MemoryStream())
                    {
                        fs.CopyTo(ms);
                        bytes = ms.ToArray();
                    }

                    context.Response.ContentType = "image/jpeg";
                    context.Response.ContentLength64 = bytes.Length;
                    context.Response.StatusCode = (int)HttpStatusCode.OK;

                    // 모바일 클라이언트 EOF 인식 및 CORS 허용
                    context.Response.Headers.Add("Access-Control-Allow-Origin", "*");
                    context.Response.Headers.Add("Connection", "close");

                    context.Response.OutputStream.Write(bytes, 0, bytes.Length);
                    context.Response.OutputStream.Flush();
                    System.Diagnostics.Debug.WriteLine($"[웹서버 전송 완료]: {fileName} ({bytes.Length} bytes)");
                }
                else
                {
                    context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    System.Diagnostics.Debug.WriteLine($"[웹서버 404]: {filePath}");
                }
            }
            catch (Exception ex)
            {
                try { context.Response.StatusCode = (int)HttpStatusCode.InternalServerError; } catch { }
                System.Diagnostics.Debug.WriteLine($"[웹서버 예외]: {ex.Message}");
            }
            finally
            {
                try
                {
                    // 안드로이드 이미지 로더가 전송 완료를 즉시 감지하도록 명시적 종료
                    context.Response.Close();
                }
                catch { }
            }
        }

        // 4. 안전한 웹서버 중지 메서드 (크래시 방어)
        public static void StopEmbeddedWebServer()
        {
            try
            {
                _serverCts?.Cancel();
            }
            catch { }

            try
            {
                if (_httpListener != null && _httpListener.IsListening)
                {
                    _httpListener.Stop();
                }
            }
            catch { }

            try
            {
                _httpListener?.Close();
            }
            catch { }
            finally
            {
                _httpListener = null;
                _serverCts = null;
                System.Diagnostics.Debug.WriteLine("[웹서버 종료] 정상적으로 정지 및 리소스 해제 완료");
            }
        }

        // 5. Firebase Admin SDK 초기화
        private static void EnsureFirebase()
        {
            if (_isFirebaseInitialized) return;

            lock (LockObj)
            {
                if (_isFirebaseInitialized) return;

                string keyPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "serviceAccountKey.json");
                if (!File.Exists(keyPath))
                {
                    throw new FileNotFoundException($"Firebase 키 파일을 찾을 수 없습니다: {keyPath}");
                }

                if (FirebaseApp.DefaultInstance == null)
                {
                    FirebaseApp.Create(new AppOptions
                    {
                        Credential = GoogleCredential.FromFile(keyPath)
                    });
                }
                _isFirebaseInitialized = true;
            }
        }

        // 6. FCM 푸시 발송 (노트북 Wi-Fi IP 주소 전송)
        public static async Task<string> SendRipCurrentAlertAsync(
            string snapshotPath,
            int dangerCount,
            int totalCount,
            int ripCount,
            string locationName = "해운대 해수욕장",
            string riskLevel = "emergency",
            string cameraId = "CCTV-E01",
            double latitude = 35.1587,
            double longitude = 129.1604)
        {
            EnsureFirebase();

            string fileName = Path.GetFileName(snapshotPath);

            // http://192.168.0.34:8501/app/static/snapshots/파일명.jpg 형식 생성
            string imageUrl = $"{BaseUrl}/app/static/snapshots/{fileName}";
            string detectedAt = DateTime.Now.ToString("HH:mm:ss");

            var message = new Message
            {
                Topic = TOPIC,
                Android = new AndroidConfig
                {
                    Priority = Priority.High
                },
                Data = new Dictionary<string, string>
                {
                    { "title", "⚠️ 이안류 위험 경보 발생!" },
                    { "body", $"{locationName}에서 이안류 위험이 감지되었습니다." },
                    { "location_name", locationName },
                    { "person_count", dangerCount.ToString() },
                    { "latitude", latitude.ToString() },
                    { "longitude", longitude.ToString() },
                    { "image_url", imageUrl },
                    { "risk_level", riskLevel },
                    { "persons_in_rip", dangerCount.ToString() },
                    { "total_persons", totalCount.ToString() },
                    { "rip_count", ripCount.ToString() },
                    { "zone", locationName },
                    { "camera_id", cameraId },
                    { "detected_at", detectedAt }
                }
            };

            return await FirebaseMessaging.DefaultInstance.SendAsync(message);
        }
    }
}