using System;

namespace OpenUtau.Core.Util {
    // Switches for hosts without a GUI (OpenUtau.Cli). The app leaves all of them unset.
    public static class Headless {
        // Must be set before Preferences or NotePresets are first used: both load, and may save, on first use.
        public static bool ReadOnlyUserData;
        public static string? CachePath;
        // Applied to a model's bytes before its session is created.
        public static Func<byte[], byte[]>? OnnxModelFilter;
        public static uint? NoiseSeed;
    }
}
