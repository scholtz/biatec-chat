# R8 rules for the release build. The Flutter Gradle plugin adds the Flutter engine's own rules.
# KeystorePlugin is instantiated directly from MainActivity, so it needs no keep rule.

# No Java or Kotlin logging in the release build: calls to android.util.Log (from this app and the libraries it bundles) are removed.
# Only the call goes: an argument expression R8 cannot prove free of side effects (a library's obj.describe(), say) is still evaluated,
# though its text never reaches logcat.
# This does not reach Dart print/debugPrint output or native logging from the Flutter engine and NDK libraries, which go to logcat
# by their own path; the Dart code is kept free of logging that could carry a secret, code, token or key instead.
-assumenosideeffects class android.util.Log {
    public static int v(...);
    public static int d(...);
    public static int i(...);
    public static int w(...);
    public static int e(...);
    public static int wtf(...);
    public static int println(...);
    public static java.lang.String getStackTraceString(...);
}
# A library's `if (Log.isLoggable(...))` guard is then false, so its message is not even built.
-assumenosideeffects class android.util.Log {
    public static boolean isLoggable(...) return false;
}
