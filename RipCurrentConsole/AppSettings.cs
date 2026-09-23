using System;
using System.IO;
using System.Text.Json;

namespace RipCurrentConsole
{
    public class AppSettings
    {
        // 1. 이안류 모델 파라미터
        public float RipConf { get; set; } = 0.25f;
        public float RipIou { get; set; } = 0.70f;

        // 2. Swimmer 모델 파라미터
        public float SwimmerConf { get; set; } = 0.10f;
        public float SwimmerIou { get; set; } = 0.45f;

        // 3. 필터링 및 세션 파라미터
        public double SameAreaSuppressSec { get; set; } = 13.0;     // 동일 구역 재캡처 억제 시간 (기본 13초)
        public double SessionClearDelaySec { get; set; } = 4.0;     // 완전 무위험 시 세션 종료 (기본 4초)
        public double SpatialDistanceThreshold { get; set; } = 220.0; // 동일인 판정 반경 (기본 220px)
    }

    public static class ConfigManager
    {
        private static readonly string ConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
        private static AppSettings? _currentSettings;

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath);
                    _currentSettings = JsonSerializer.Deserialize<AppSettings>(json);
                    if (_currentSettings != null) return _currentSettings;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[설정 파일 로드 실패]: {ex.Message}");
            }

            _currentSettings = new AppSettings();
            Save(_currentSettings);
            return _currentSettings;
        }

        public static void Save(AppSettings settings)
        {
            try
            {
                _currentSettings = settings;
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(settings, options);
                File.WriteAllText(ConfigPath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[설정 파일 저장 실패]: {ex.Message}");
            }
        }
    }
}