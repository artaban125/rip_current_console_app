using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace RipCurrentConsole
{
    public partial class LogViewerWindow : Window
    {
        public LogViewerWindow()
        {
            InitializeComponent();
            LoadLogs();
        }

        // DB로부터 로그 불러오기
        public void LoadLogs()
        {
            var logs = DatabaseLogger.GetAllLogs();
            GridLogs.ItemsSource = logs;
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            LoadLogs();
        }

        // ⭐️ 이미지 파일명 링크 클릭 시 팝업 띄우기
        private void BtnImageLink_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is EmergencyLogItem item)
            {
                if (!File.Exists(item.ImagePath))
                {
                    MessageBox.Show($"파일을 찾을 수 없습니다:\n{item.ImagePath}", "알림", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                try
                {
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.UriSource = new Uri(item.ImagePath);
                    bi.EndInit();
                    bi.Freeze();

                    ModalPreviewImage.Source = bi;
                    TxtModalTitle.Text = $"🚨 [{item.CreatedAt}] {item.ImageFileName} ({item.TimestampSec:F1}초 시점 - 위험 {item.DangerCount}명)";
                    ImageModal.Visibility = Visibility.Visible;
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"이미지 로드 실패: {ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void BtnCloseModal_Click(object sender, RoutedEventArgs e)
        {
            ImageModal.Visibility = Visibility.Collapsed;
            ModalPreviewImage.Source = null;
        }
    }
}