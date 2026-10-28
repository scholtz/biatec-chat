# R8 rules for the release build. The Flutter Gradle plugin adds the Flutter engine's own rules.
# KeystorePlugin is instantiated directly from MainActivity, so it needs no keep rule.

# No logging in the release build: calls to android.util.Log are removed, so nothing a library or this app logs reaches logcat.
-assumenosideeffects class android.util.Log {
    public static int v(...);
    public static int d(...);
    public static int i(...);
    public static int w(...);
    public static int e(...);
}
# Smaller and harder to read: classes are renamed into one package and made as private as they can be.
-repackageclasses ''
-allowaccessmodification
