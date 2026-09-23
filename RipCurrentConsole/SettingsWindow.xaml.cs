using System.Windows;

namespace RipCurrentConsole
{
    public partial class SettingsWindow : Window
    {
        public SettingsWindow()
        {
            InitializeComponent();
            LoadCurrentSettings();
        }

        private void LoadCurrentSettings()
        {
            var config = ConfigManager.Load();
            SliderRipConf.Value = config.RipConf;
            SliderRipIou.Value = config.RipIou;
            SliderSwimmerConf.Value = config.SwimmerConf;
            SliderSwimmerIou.Value = config.SwimmerIou;
            SliderSameAreaSec.Value = config.SameAreaSuppressSec;
            SliderClearDelaySec.Value = config.SessionClearDelaySec;
            SliderSpatialDist.Value = config.SpatialDistanceThreshold;
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            var settings = new AppSettings
            {
                RipConf = (float)SliderRipConf.Value,
                RipIou = (float)SliderRipIou.Value,
                SwimmerConf = (float)SliderSwimmerConf.Value,
                SwimmerIou = (float)SliderSwimmerIou.Value,
                SameAreaSuppressSec = SliderSameAreaSec.Value,
                SessionClearDelaySec = SliderClearDelaySec.Value,
                SpatialDistanceThreshold = SliderSpatialDist.Value
            };

            ConfigManager.Save(settings);
            MessageBox.Show("환경설정이 안전하게 저장되었습니다.\n다음 관제 시작 시 즉시 적용됩니다.", "저장 완료", MessageBoxButton.OK, MessageBoxImage.Information);
            this.Close();
        }

        private void BtnReset_Click(object sender, RoutedEventArgs e)
        {
            // 사용자가 지정한 기본값으로 복원
            SliderRipConf.Value = 0.25;
            SliderRipIou.Value = 0.70;
            SliderSwimmerConf.Value = 0.10;
            SliderSwimmerIou.Value = 0.45;
            SliderSameAreaSec.Value = 13.0;
            SliderClearDelaySec.Value = 4.0;
            SliderSpatialDist.Value = 220.0;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}