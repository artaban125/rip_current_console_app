using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace RipCurrentConsole
{
    // DataGrid 및 관리 연동용 로그 모델
    public class EmergencyLogItem
    {
        public long Id { get; set; }
        public string CreatedAt { get; set; } = string.Empty;
        public string DangerLevel { get; set; } = string.Empty;
        public int RipCount { get; set; }
        public int DangerCount { get; set; }
        public int TotalCount { get; set; }
        public double TimestampSec { get; set; }
        public string ImagePath { get; set; } = string.Empty;
        public string ImageFileName => Path.GetFileName(ImagePath);
        public string AlertReason { get; set; } = string.Empty;
        public string DispatchStatus { get; set; } = string.Empty;
        public string IsConfirmed { get; set; } = "미확인"; // ⭐️ 관리자 확인 여부 ('미확인' / '확인')
    }

    public static class DatabaseLogger
    {
        private static readonly string DbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "surveillance_logs.db");
        private static readonly string ConnectionString = $"Data Source={DbPath}";
        private static readonly object LockObj = new object();

        // 1. DB 및 테이블 생성 ('is_confirmed' 컬럼 추가)
        public static void InitializeDatabase()
        {
            lock (LockObj)
            {
                using var conn = new SqliteConnection(ConnectionString);
                conn.Open();

                string createTableQuery = @"
                    CREATE TABLE IF NOT EXISTS emergency_logs (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        created_at TEXT NOT NULL,
                        danger_level TEXT NOT NULL,
                        rip_count INTEGER NOT NULL,
                        danger_count INTEGER NOT NULL,
                        total_count INTEGER NOT NULL,
                        timestamp_sec REAL NOT NULL,
                        image_path TEXT NOT NULL,
                        alert_reason TEXT NOT NULL DEFAULT '자동',
                        dispatch_status TEXT NOT NULL DEFAULT '대기',
                        is_confirmed TEXT NOT NULL DEFAULT '미확인'
                    );";

                using var cmd = new SqliteCommand(createTableQuery, conn);
                cmd.ExecuteNonQuery();
            }
        }

        // 2. 신규 로그 삽입 (초기 확인 여부: '미확인')
        public static long InsertEmergencyLog(
            string dangerLevel,
            int ripCount,
            int dangerCount,
            int totalCount,
            double timestampSec,
            string imagePath,
            string dispatchStatus = "대기",
            string isConfirmed = "미확인")
        {
            lock (LockObj)
            {
                try
                {
                    using var conn = new SqliteConnection(ConnectionString);
                    conn.Open();

                    string insertQuery = @"
                        INSERT INTO emergency_logs 
                        (created_at, danger_level, rip_count, danger_count, total_count, timestamp_sec, image_path, alert_reason, dispatch_status, is_confirmed)
                        VALUES 
                        ($created_at, $danger_level, $rip_count, $danger_count, $total_count, $timestamp_sec, $image_path, '자동', $dispatch_status, $is_confirmed);
                        SELECT last_insert_rowid();";

                    using var cmd = new SqliteCommand(insertQuery, conn);
                    cmd.Parameters.AddWithValue("$created_at", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                    cmd.Parameters.AddWithValue("$danger_level", dangerLevel);
                    cmd.Parameters.AddWithValue("$rip_count", ripCount);
                    cmd.Parameters.AddWithValue("$danger_count", dangerCount);
                    cmd.Parameters.AddWithValue("$total_count", totalCount);
                    cmd.Parameters.AddWithValue("$timestamp_sec", Math.Round(timestampSec, 2));
                    cmd.Parameters.AddWithValue("$image_path", imagePath);
                    cmd.Parameters.AddWithValue("$dispatch_status", dispatchStatus);
                    cmd.Parameters.AddWithValue("$is_confirmed", isConfirmed);

                    object? result = cmd.ExecuteScalar();
                    return result != null ? (long)result : -1;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[DB 로그 저장 실패]: {ex.Message}");
                    return -1;
                }
            }
        }

        // 3. ⭐️ 관리자 확인 여부 업데이트 ('확인' 또는 '미확인')
        public static void UpdateConfirmStatus(long logId, string confirmStatus)
        {
            if (logId <= 0) return;
            lock (LockObj)
            {
                try
                {
                    using var conn = new SqliteConnection(ConnectionString);
                    conn.Open();

                    string updateQuery = "UPDATE emergency_logs SET is_confirmed = $status WHERE id = $id;";
                    using var cmd = new SqliteCommand(updateQuery, conn);
                    cmd.Parameters.AddWithValue("$status", confirmStatus);
                    cmd.Parameters.AddWithValue("$id", logId);
                    cmd.ExecuteNonQuery();
                    System.Diagnostics.Debug.WriteLine($"[DB 업데이트 성공] Log ID {logId} 확인 상태 -> {confirmStatus}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[DB 확인상태 업데이트 실패]: {ex.Message}");
                }
            }
        }

        // 4. 발송 상태 업데이트
        public static void UpdateDispatchStatus(long logId, string status)
        {
            if (logId <= 0) return;
            lock (LockObj)
            {
                try
                {
                    using var conn = new SqliteConnection(ConnectionString);
                    conn.Open();

                    string updateQuery = "UPDATE emergency_logs SET dispatch_status = $status WHERE id = $id;";
                    using var cmd = new SqliteCommand(updateQuery, conn);
                    cmd.Parameters.AddWithValue("$status", status);
                    cmd.Parameters.AddWithValue("$id", logId);
                    cmd.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[DB 상태 업데이트 실패]: {ex.Message}");
                }
            }
        }

        // 5. 전체 로그 최신순 조회
        public static List<EmergencyLogItem> GetAllLogs()
        {
            var list = new List<EmergencyLogItem>();
            lock (LockObj)
            {
                try
                {
                    using var conn = new SqliteConnection(ConnectionString);
                    conn.Open();

                    string selectQuery = @"
                        SELECT id, created_at, danger_level, rip_count, danger_count, total_count, 
                               timestamp_sec, image_path, alert_reason, dispatch_status, is_confirmed 
                        FROM emergency_logs 
                        ORDER BY id DESC;";

                    using var cmd = new SqliteCommand(selectQuery, conn);
                    using var reader = cmd.ExecuteReader();

                    while (reader.Read())
                    {
                        list.Add(new EmergencyLogItem
                        {
                            Id = reader.GetInt64(0),
                            CreatedAt = reader.GetString(1),
                            DangerLevel = reader.GetString(2),
                            RipCount = reader.GetInt32(3),
                            DangerCount = reader.GetInt32(4),
                            TotalCount = reader.GetInt32(5),
                            TimestampSec = reader.GetDouble(6),
                            ImagePath = reader.GetString(7),
                            AlertReason = reader.GetString(8),
                            DispatchStatus = reader.GetString(9),
                            IsConfirmed = reader.GetString(10)
                        });
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[DB 전체 로그 조회 실패]: {ex.Message}");
                }
            }
            return list;
        }
    }
}