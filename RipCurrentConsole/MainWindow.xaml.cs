using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using OpenCvSharp;

// WPF와 OpenCvSharp 간의 클래스명 충돌 방지용 별칭
using WpfWindow = System.Windows.Window;
using WpfImage = System.Windows.Controls.Image;
using CvRect = OpenCvSharp.Rect;
using CvPoint = OpenCvSharp.Point;

namespace RipCurrentConsole
{
    public partial class MainWindow : WpfWindow
    {
        private string _videoPath = string.Empty;
        private CancellationTokenSource? _cts;
        private bool _isMonitoring = false;

        // 세션 및 깜빡임 억제 런타임 제어 변수
        private bool _isInEmergencySession = false;
        private int _sessionMaxDangerCount = 0;
        private double _lastDangerDetectedTimeSec = -999.0;
        private double _lastCapturedTimeSec = -999.0;
        private int _consecutiveDangerFrames = 0;
        private List<CvPoint> _lastCapturedDangerPoints = new List<CvPoint>();
        private int _alertSeq = 0;

        // 최근 스냅샷 경로 보관
        private string? _latestSnapshotPath = null;

        // 모델 파일명
        private const string RIP_MODEL_PATH = "rip_current.onnx";
        private const string SWIMMER_MODEL_PATH = "swimmer.onnx";

        public MainWindow()
        {
            InitializeComponent();

            // 1. SQLite 데이터베이스 및 테이블 초기화
            DatabaseLogger.InitializeDatabase();

            // 2. 안드로이드 앱 스냅샷 서빙용 내장 경량 웹서버 시작 (8501 포트)
            string snapshotDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "snapshots");
            if (!Directory.Exists(snapshotDir)) Directory.CreateDirectory(snapshotDir);
            AlertDispatcher.StartEmbeddedWebServer(snapshotDir);
        }

        // 윈도우 종료 시 내장 웹서버 정리
        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            AlertDispatcher.StopEmbeddedWebServer();
        }

        // [비율 제한] 우측 패널 최대 너비 30% 제한
        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            double availableWidth = this.ActualWidth - 24 - 10;
            if (availableWidth > 0)
            {
                ColGallery.MaxWidth = Math.Max(ColGallery.MinWidth, availableWidth * 0.30);
            }
        }

        // 1. 관제 영상 파일 선택 (이안류 감시 준비)
        private void BtnLoadVideo_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Video Files|*.mp4;*.avi;*.mov;*.mkv"
            };

            if (dlg.ShowDialog() == true)
            {
                _videoPath = dlg.FileName;
                TxtStatus.Text = $"대기 중: {Path.GetFileName(_videoPath)}";
                TxtStatus.Visibility = Visibility.Visible;
                BtnToggleSurveillance.IsEnabled = true;
            }
        }

        // 2. 이안류 감시 시작 / 중지 토글
        private void BtnToggleSurveillance_Click(object sender, RoutedEventArgs e)
        {
            if (!_isMonitoring)
            {
                _isMonitoring = true;
                _cts = new CancellationTokenSource();
                BtnToggleSurveillance.Content = "⏹ 감시 중지";
                BtnToggleSurveillance.Background = new SolidColorBrush(Colors.DarkGray);
                TxtStatus.Visibility = Visibility.Collapsed;

                Task.Run(() => ProcessingLoop(_videoPath, _cts.Token));
            }
            else
            {
                StopSurveillance();
            }
        }

        private void StopSurveillance()
        {
            _isMonitoring = false;
            _cts?.Cancel();
            BtnToggleSurveillance.Content = "🚨 이안류 감시 시작";
            BtnToggleSurveillance.Background = new SolidColorBrush(Color.FromRgb(243, 139, 168));

            _isInEmergencySession = false;
            _sessionMaxDangerCount = 0;
            _lastDangerDetectedTimeSec = -999.0;
            _lastCapturedTimeSec = -999.0;
            _consecutiveDangerFrames = 0;
            _lastCapturedDangerPoints.Clear();

            Dispatcher.Invoke(() => UpdateAlertBanner(hasRip: false, dangerCount: 0, totalCount: 0));
        }

        // "관제 로그 보기" 버튼 클릭
        private void BtnOpenLogViewer_Click(object sender, RoutedEventArgs e)
        {
            var logWin = new LogViewerWindow { Owner = this };
            logWin.ShowDialog();
        }

        // "관제 환경설정" 버튼 클릭
        private void BtnOpenSettings_Click(object sender, RoutedEventArgs e)
        {
            var settingsWin = new SettingsWindow { Owner = this };
            settingsWin.ShowDialog();
        }

        // 3. 메인 분석 및 재생 루프
        private void ProcessingLoop(string videoPath, CancellationToken token)
        {
            var cfg = ConfigManager.Load();

            string ripPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, RIP_MODEL_PATH);
            string swimmerPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, SWIMMER_MODEL_PATH);

            if (!File.Exists(ripPath) || !File.Exists(swimmerPath))
            {
                Dispatcher.Invoke(() =>
                {
                    MessageBox.Show("모델 파일을 찾을 수 없습니다.\n실행 폴더에 모델 파일이 있는지 확인하세요.", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
                    StopSurveillance();
                });
                return;
            }

            string snapshotDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "snapshots");
            if (!Directory.Exists(snapshotDir)) Directory.CreateDirectory(snapshotDir);

            using var ripDetector = new YoloDetector(ripPath);
            using var swimmerDetector = new YoloDetector(swimmerPath);

            using var capture = new VideoCapture(videoPath);
            if (!capture.IsOpened()) return;

            using var frame = new Mat();
            using var displayMat = new Mat();

            double fps = capture.Fps > 0 ? capture.Fps : 30.0;
            int frameIdx = 0;
            int processedIdx = 0;

            var cachedRips = new List<DetectionBox>();

            while (!token.IsCancellationRequested)
            {
                if (!capture.Read(frame) || frame.Empty()) break;

                frameIdx++;
                if (frameIdx % 3 != 0) continue;
                processedIdx++;

                double currentSec = frameIdx / fps;

                // [구간 1] 이안류 영역 추론
                if (processedIdx % 3 == 1 || cachedRips.Count == 0)
                {
                    var newRips = ripDetector.Detect(frame, confThreshold: cfg.RipConf, iouThreshold: cfg.RipIou);
                    if (newRips.Count > 0) cachedRips = newRips;
                }

                // [구간 2] 수영객 추론
                var swimmers = swimmerDetector.Detect(frame, confThreshold: cfg.SwimmerConf, iouThreshold: cfg.SwimmerIou);

                // [구간 3] 박스 교차 침범 판정
                bool dangerDetected = false;
                int dangerCount = 0;
                int totalCount = swimmers.Count;
                int ripCount = cachedRips.Count;
                bool hasRip = ripCount > 0;
                var currentDangerPoints = new List<CvPoint>();

                foreach (var rip in cachedRips)
                {
                    var r = new CvRect((int)rip.Box.X, (int)rip.Box.Y, (int)rip.Box.Width, (int)rip.Box.Height);
                    Cv2.Rectangle(frame, r, new Scalar(255, 120, 0), 2);
                    Cv2.PutText(frame, $"RIP {rip.Confidence:P0}", new CvPoint(r.X, Math.Max(20, r.Y - 5)),
                        HersheyFonts.HersheySimplex, 0.6, new Scalar(255, 120, 0), 2);
                }

                foreach (var swimmer in swimmers)
                {
                    var sRect = new CvRect((int)swimmer.Box.X, (int)swimmer.Box.Y, (int)swimmer.Box.Width, (int)swimmer.Box.Height);
                    bool inRip = false;

                    foreach (var rip in cachedRips)
                    {
                        var ripRect = new CvRect((int)rip.Box.X, (int)rip.Box.Y, (int)rip.Box.Width, (int)rip.Box.Height);
                        var intersect = sRect & ripRect;

                        if (intersect.Width > 0 && intersect.Height > 0)
                        {
                            inRip = true;
                            break;
                        }
                    }

                    if (inRip)
                    {
                        dangerDetected = true;
                        dangerCount++;
                        currentDangerPoints.Add(new CvPoint(sRect.X + sRect.Width / 2, sRect.Y + sRect.Height / 2));
                        Cv2.Rectangle(frame, sRect, Scalar.Red, 3);
                        Cv2.PutText(frame, "DANGER", new CvPoint(sRect.X, Math.Max(20, sRect.Y - 5)),
                            HersheyFonts.HersheySimplex, 0.6, Scalar.Red, 2);
                    }
                    else
                    {
                        Cv2.Rectangle(frame, sRect, Scalar.LimeGreen, 2);
                    }
                }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    UpdateAlertBanner(hasRip, dangerCount, totalCount);
                }));

                // [구간 4] 환경설정 파라미터 기반 스마트 사고 세션 필터링
                if (dangerDetected)
                {
                    _consecutiveDangerFrames++;
                    _lastDangerDetectedTimeSec = currentSec;

                    Cv2.PutText(frame, $"EMERGENCY! {dangerCount} IN RIP", new CvPoint(30, 80),
                        HersheyFonts.HersheySimplex, 1.0, Scalar.Red, 3);

                    bool shouldCapture = false;

                    if (_consecutiveDangerFrames >= 3)
                    {
                        if (!_isInEmergencySession)
                        {
                            _isInEmergencySession = true;
                            _sessionMaxDangerCount = dangerCount;
                            shouldCapture = true;
                        }
                        else
                        {
                            bool isNewLocation = false;
                            foreach (var pt in currentDangerPoints)
                            {
                                bool matchedNear = false;
                                foreach (var oldPt in _lastCapturedDangerPoints)
                                {
                                    double dist = Math.Sqrt(Math.Pow(pt.X - oldPt.X, 2) + Math.Pow(pt.Y - oldPt.Y, 2));
                                    if (dist < cfg.SpatialDistanceThreshold)
                                    {
                                        matchedNear = true;
                                        break;
                                    }
                                }
                                if (!matchedNear)
                                {
                                    isNewLocation = true;
                                }
                            }

                            if (dangerCount > _sessionMaxDangerCount)
                            {
                                _sessionMaxDangerCount = dangerCount;
                                shouldCapture = true;
                            }
                            else if (isNewLocation && (currentSec - _lastCapturedTimeSec >= 4.0))
                            {
                                shouldCapture = true;
                            }
                            else if (currentSec - _lastCapturedTimeSec >= cfg.SameAreaSuppressSec)
                            {
                                shouldCapture = true;
                            }
                        }
                    }

                    if (shouldCapture)
                    {
                        _lastCapturedTimeSec = currentSec;
                        _lastCapturedDangerPoints = new List<CvPoint>(currentDangerPoints);
                        _alertSeq++;

                        var snapMat = frame.Clone();
                        double alertSec = currentSec;
                        int currentSeq = _alertSeq;
                        int currentDangerCount = dangerCount;
                        int currentTotalCount = totalCount;
                        int currentRipCount = ripCount;

                        Task.Run(() => SaveSnapshotAndLogToDatabase(
                            snapMat, alertSec, currentSeq, currentRipCount, currentDangerCount, currentTotalCount, snapshotDir));
                    }
                }
                else
                {
                    _consecutiveDangerFrames = 0;

                    if (_isInEmergencySession && (currentSec - _lastDangerDetectedTimeSec >= cfg.SessionClearDelaySec))
                    {
                        _isInEmergencySession = false;
                        _sessionMaxDangerCount = 0;
                        _lastCapturedTimeSec = -999.0;
                        _lastCapturedDangerPoints.Clear();
                    }
                }

                // [구간 5] 화면 렌더링
                Cv2.Resize(frame, displayMat, new OpenCvSharp.Size(960, 540));
                var displayBmp = MatToBitmapSource(displayMat);

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    MainVideoPlayer.Source = displayBmp;
                }));
            }

            Dispatcher.Invoke(() =>
            {
                StopSurveillance();
                TxtStatus.Text = "관제가 완료되었습니다 (동영상 재생 종료).";
                TxtStatus.Visibility = Visibility.Visible;
            });
        }

        // 3단계 등급별 알림바 갱신
        private void UpdateAlertBanner(bool hasRip, int dangerCount, int totalCount)
        {
            if (dangerCount > 0)
            {
                AlertBanner.Background = new SolidColorBrush(Color.FromRgb(46, 20, 28));
                AlertBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(243, 139, 168));
                BadgeLevel.Background = new SolidColorBrush(Color.FromRgb(243, 139, 168));
                TxtLevelBadge.Text = "긴급";
                TxtLevelBadge.Foreground = new SolidColorBrush(Color.FromRgb(17, 17, 27));

                TxtBannerDetails.Text = $"이안류 발생  |  위험 인원 {dangerCount}명  |  전체 {totalCount}명";
                TxtBannerDetails.Foreground = new SolidColorBrush(Color.FromRgb(243, 139, 168));
            }
            else if (hasRip)
            {
                AlertBanner.Background = new SolidColorBrush(Color.FromRgb(48, 35, 20));
                AlertBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(250, 179, 135));
                BadgeLevel.Background = new SolidColorBrush(Color.FromRgb(250, 179, 135));
                TxtLevelBadge.Text = "경고";
                TxtLevelBadge.Foreground = new SolidColorBrush(Color.FromRgb(17, 17, 27));

                TxtBannerDetails.Text = $"이안류 발생  |  전체 {totalCount}명";
                TxtBannerDetails.Foreground = new SolidColorBrush(Color.FromRgb(250, 179, 135));
            }
            else
            {
                AlertBanner.Background = new SolidColorBrush(Color.FromRgb(26, 44, 38));
                AlertBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(166, 227, 161));
                BadgeLevel.Background = new SolidColorBrush(Color.FromRgb(166, 227, 161));
                TxtLevelBadge.Text = "관찰";
                TxtLevelBadge.Foreground = new SolidColorBrush(Color.FromRgb(17, 17, 27));

                TxtBannerDetails.Text = $"이안류 없음  |  탐지인원 {totalCount}명";
                TxtBannerDetails.Foreground = new SolidColorBrush(Color.FromRgb(166, 227, 161));
            }
        }

        // ⭐️ [비동기 파이프라인] 스냅샷 저장 -> DB 기록 -> FCM 발송 -> 1줄 인라인 카드 배치
        private void SaveSnapshotAndLogToDatabase(
            Mat snapMat, double timestampSec, int seq, int ripCount, int dangerCount, int totalCount, string snapshotDir)
        {
            try
            {
                string fileName = $"alert_{DateTime.Now:yyyyMMdd_HHmmss_fff}_#{seq}_{timestampSec:F1}s.jpg";
                string fullSavePath = Path.Combine(snapshotDir, fileName);

                // 1) 스냅샷 디스크 저장
                Cv2.ImWrite(fullSavePath, snapMat);
                _latestSnapshotPath = fullSavePath;

                // 2) SQLite DB에 긴급 로그 저장 (초기 발송 상태: '대기')
                long logId = DatabaseLogger.InsertEmergencyLog(
                    dangerLevel: "긴급",
                    ripCount: ripCount,
                    dangerCount: dangerCount,
                    totalCount: totalCount,
                    timestampSec: timestampSec,
                    imagePath: fullSavePath,
                    dispatchStatus: "대기",
                    isConfirmed: "미확인"
                );

                // 3) 안드로이드 앱으로 비동기 FCM 발송 및 결과 업데이트
                Task.Run(async () =>
                {
                    try
                    {
                        string messageId = await AlertDispatcher.SendRipCurrentAlertAsync(
                            snapshotPath: fullSavePath,
                            dangerCount: dangerCount,
                            totalCount: totalCount,
                            ripCount: ripCount,
                            locationName: "해운대 해수욕장"
                        );

                        DatabaseLogger.UpdateDispatchStatus(logId, $"발송성공({messageId})");
                        System.Diagnostics.Debug.WriteLine($"[FCM 발송 성공] Message ID: {messageId}");
                    }
                    catch (Exception fcmEx)
                    {
                        DatabaseLogger.UpdateDispatchStatus(logId, $"발송실패: {fcmEx.Message}");
                        System.Diagnostics.Debug.WriteLine($"[FCM 발송 실패]: {fcmEx.Message}");
                    }
                });

                // 4) ⭐️ UI 갤러리 카드 생성 (1줄 인라인 구조)
                Cv2.ImEncode(".jpg", snapMat, out byte[] imageBytes);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        var bitmap = new BitmapImage();
                        using (var ms = new MemoryStream(imageBytes))
                        {
                            bitmap.BeginInit();
                            bitmap.CacheOption = BitmapCacheOption.OnLoad;
                            bitmap.StreamSource = ms;
                            bitmap.EndInit();
                        }
                        bitmap.Freeze();

                        // 컴팩트한 카드 외곽 프레임
                        var card = new Border
                        {
                            Background = new SolidColorBrush(Color.FromRgb(30, 30, 46)),
                            BorderBrush = new SolidColorBrush(Color.FromRgb(49, 50, 68)),
                            BorderThickness = new Thickness(1),
                            CornerRadius = new CornerRadius(6),
                            Margin = new Thickness(0, 0, 0, 8),
                            Padding = new Thickness(6),
                            HorizontalAlignment = HorizontalAlignment.Stretch
                        };

                        var stack = new StackPanel();

                        // 썸네일 이미지 영역
                        var imgBorder = new Border
                        {
                            CornerRadius = new CornerRadius(4),
                            ClipToBounds = true,
                            HorizontalAlignment = HorizontalAlignment.Stretch,
                            Background = new SolidColorBrush(Color.FromRgb(17, 17, 27)),
                            Cursor = Cursors.Hand
                        };
                        imgBorder.MouseLeftButtonDown += (s, e) => ShowSnapshotModal(fullSavePath, timestampSec, dangerCount);

                        var imgControl = new WpfImage
                        {
                            Source = bitmap,
                            Stretch = Stretch.Uniform,
                            HorizontalAlignment = HorizontalAlignment.Center
                        };
                        imgBorder.Child = imgControl;

                        // ⭐️ [1줄 인라인 바] 텍스트 정보(좌측) + 초소형 확인 버튼(우측)
                        var inlineBar = new DockPanel
                        {
                            Margin = new Thickness(2, 6, 2, 2),
                            LastChildFill = false
                        };

                        // 좌측: 아이콘 없이 핵심 위험 정보만 표시
                        var labelInfo = new TextBlock
                        {
                            Text = $"[#{seq}] 위험 {dangerCount}명 ({timestampSec:F1}s)",
                            Foreground = new SolidColorBrush(Color.FromRgb(243, 139, 168)),
                            FontWeight = FontWeights.SemiBold,
                            FontSize = 12,
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        DockPanel.SetDock(labelInfo, Dock.Left);

                        // 우측: 초소형 슬림 확인 버튼
                        var btnConfirm = new Button
                        {
                            Content = "확인",
                            Style = (Style)FindResource("SlimConfirmButtonStyle")
                        };
                        DockPanel.SetDock(btnConfirm, Dock.Right);

                        btnConfirm.Click += (s, e) =>
                        {
                            Task.Run(() => DatabaseLogger.UpdateConfirmStatus(logId, "확인"));
                            SnapshotGallery.Children.Remove(card);
                        };

                        inlineBar.Children.Add(labelInfo);
                        inlineBar.Children.Add(btnConfirm);

                        // 스택에 이미지와 1줄 바만 추가
                        stack.Children.Add(imgBorder);
                        stack.Children.Add(inlineBar);
                        card.Child = stack;

                        SnapshotGallery.Children.Insert(0, card);
                    }
                    catch (Exception uiEx)
                    {
                        System.Diagnostics.Debug.WriteLine($"[UI 카드 렌더링 실패]: {uiEx.Message}");
                    }
                }));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[스냅샷 및 로그 처리 실패]: {ex.Message}");
            }
            finally
            {
                snapMat.Dispose();
            }
        }

        // 분석 영상 클릭 시 최근 스냅샷 팝업
        private void MainVideoPlayer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (string.IsNullOrEmpty(_latestSnapshotPath) || !File.Exists(_latestSnapshotPath))
            {
                MessageBox.Show("현재까지 포착된 긴급 조난 스냅샷 이미지가 없습니다.", "안내", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ShowSnapshotModal(_latestSnapshotPath, _lastDangerDetectedTimeSec, _sessionMaxDangerCount);
        }

        private void ShowSnapshotModal(string imagePath, double timestampSec, int dangerCount)
        {
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.UriSource = new Uri(imagePath);
                bi.EndInit();
                bi.Freeze();

                ModalImagePreview.Source = bi;
                TxtModalTitle.Text = $"🚨 긴급 조난 분석 스냅샷 ({timestampSec:F1}초 시점 - 위험 {dangerCount}명) | {Path.GetFileName(imagePath)}";
                SnapshotPopupModal.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"이미지 로드 오류: {ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnCloseModal_Click(object sender, RoutedEventArgs e)
        {
            SnapshotPopupModal.Visibility = Visibility.Collapsed;
            ModalImagePreview.Source = null;
        }

        private BitmapSource MatToBitmapSource(Mat mat)
        {
            using var ms = new MemoryStream();
            mat.WriteToStream(ms, ".bmp");
            ms.Position = 0;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
    }
}