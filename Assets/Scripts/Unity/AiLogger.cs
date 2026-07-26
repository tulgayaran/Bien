using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace Bien.Unity
{
    /// <summary>AI hesap/karar günlüğü: persistentDataPath/bien_ai_log.txt.
    /// Her oyun başında yeni bölüm açar; her satır anında diske yazılır.</summary>
    public static class AiLogger
    {
        private static string _path;
        private static readonly StringBuilder _buf = new();

        public static string Path => _path;

        public static void StartSession()
        {
#if UNITY_EDITOR
            // Editor: proje kökü → D:\04 unity\BienGame\bien_ai_log.txt
            _path = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(Application.dataPath, "..", "bien_ai_log.txt"));
#else
            _path = System.IO.Path.Combine(Application.persistentDataPath, "bien_ai_log.txt");
#endif
            try { File.WriteAllText(_path, ""); } catch { } // her oyunda sıfırdan başla
            Write($"================ OYUN — {DateTime.Now:yyyy-MM-dd HH:mm:ss} ================");
            UnityEngine.Debug.Log($"AI log: {_path}");
        }

        public static void Write(string line)
        {
            if (_path == null) return;
            try { File.AppendAllText(_path, line + "\n"); }
            catch (Exception e) { UnityEngine.Debug.LogWarning($"AI log yazılamadı: {e.Message}"); }
        }
    }
}