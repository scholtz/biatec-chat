/// Why a phone or this copy of the app failed the safety check, in broad groups. The user is told only that a check failed (the screen names no group, and none of the detectors): a message that says which test fired helps tune a bypass.
enum IntegrityReason {
  /// Signs that the operating system was modified (root, jailbreak).
  deviceModified,

  /// A debugger, a tracer or a hooking framework is attached.
  toolsAttached,

  /// The app runs on an emulator or a simulator.
  emulator,

  /// This copy of the app is not the one that was released (its signing certificate is not the expected one).
  appNotGenuine,
}

/// The outcome of a check: the groups that fired. Empty means healthy.
class IntegrityResult {
  const IntegrityResult(this.reasons);

  const IntegrityResult.healthy() : reasons = const {};

  final Set<IntegrityReason> reasons;

  bool get compromised => reasons.isNotEmpty;

  /// Names nothing that fired: only how many groups did.
  @override
  String toString() => 'IntegrityResult(${reasons.length} reasons)';
}

/// Looks at the phone and the app and says whether they can be trusted.
abstract class IntegrityChecker {
  Future<IntegrityResult> check();
}

/// What a detector may ask about the phone. The real one reads files, processes and the platform; tests use a fake with fixtures.
abstract class IntegrityEnvironment {
  /// Android or iOS: which indicators apply.
  IntegrityPlatform get platform;

  bool fileExists(String path);

  /// True when the app can create a file in [directory] (a folder normal apps cannot write to means the sandbox is open).
  bool canWriteTo(String directory);

  /// The text of a file (a `/proc` entry), or null when it cannot be read.
  String? readFile(String path);

  /// An Android system property, or null.
  Future<String?> property(String key);

  /// Whether a debugger is attached to the process.
  Future<bool> debuggerAttached();

  /// Whether a class with this name is loaded into the app (hooking frameworks on Android).
  Future<bool> classPresent(String name);

  /// The file names of the libraries loaded into the process (iOS).
  Future<List<String>> loadedImages();

  /// Whether the app runs in the iOS simulator.
  Future<bool> isSimulator();

  /// The SHA-256 of the app's signing certificate, lower-case hex, or null.
  Future<String?> signingDigest();
}

enum IntegrityPlatform { android, ios }
