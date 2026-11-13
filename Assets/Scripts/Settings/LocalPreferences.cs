using System.Globalization;
using UnityEngine;

namespace Protobot {
    /// <summary>Single local settings boundary used by preferences and rebinding.</summary>
    public static class LocalPreferences {
        public static bool HasKey(string key) => PlayerPrefs.HasKey(key);
        public static string GetString(string key, string fallback = "") => PlayerPrefs.GetString(key, fallback);
        public static int GetInt(string key, int fallback = 0) => PlayerPrefs.GetInt(key, fallback);
        public static float GetFloat(string key, float fallback = 0f) {
            var text = GetString(key);
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && IsFinite(value)
                ? value : fallback;
        }
        public static void SetString(string key, string value) => PlayerPrefs.SetString(key, value);
        public static void SetInt(string key, int value) => PlayerPrefs.SetInt(key, value);
        public static void SetFloat(string key, float value) => SetString(key, value.ToString("G9", CultureInfo.InvariantCulture));
        public static void DeleteKey(string key) => PlayerPrefs.DeleteKey(key);
        public static void Save() => PlayerPrefs.Save();
        public static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
