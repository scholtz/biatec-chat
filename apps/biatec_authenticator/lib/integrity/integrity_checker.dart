import 'detectors.dart';
import 'integrity.dart';

/// Runs every detector that applies to the platform and gathers the groups that fired. A detector that throws counts as a hit: an unreadable answer is not a healthy one.
class DeviceIntegrityChecker implements IntegrityChecker {
  DeviceIntegrityChecker({
    required this.environment,
    required String expectedSigningDigest,
    List<IntegrityDetector>? detectors,
  }) : detectors = detectors ?? defaultDetectors(expectedSigningDigest);

  final IntegrityEnvironment environment;
  final List<IntegrityDetector> detectors;

  static List<IntegrityDetector> defaultDetectors(
          String expectedSigningDigest) =>
      [
        const AndroidRootDetector(),
        const IosJailbreakDetector(),
        const DebuggerDetector(),
        const HookingDetector(),
        const EmulatorDetector(),
        SigningCertificateDetector(expectedSigningDigest),
      ];

  @override
  Future<IntegrityResult> check() async {
    final reasons = <IntegrityReason>{};
    for (final d in detectors) {
      if (d.platform != null && d.platform != environment.platform) continue;
      bool hit;
      try {
        hit = await d.triggered(environment);
      } on Object {
        hit = true;
      }
      if (hit) reasons.add(d.reason);
    }
    return IntegrityResult(reasons);
  }
}

/// Keeps the last result for a short time, so the check is not repeated on every touch, and runs a new one when asked ([force], on resume and before an approval).
class IntegrityGuard {
  IntegrityGuard(
    this.checker, {
    DateTime Function()? clock,
    this.validFor = const Duration(seconds: 30),
  }) : _clock = clock ?? DateTime.now;

  final IntegrityChecker checker;
  final DateTime Function() _clock;
  final Duration validFor;

  IntegrityResult? _last;
  DateTime? _at;
  Future<IntegrityResult>? _running;

  /// How many times the checker really ran (a cached answer does not count).
  int runs = 0;

  Future<IntegrityResult> current({bool force = false}) {
    final at = _at;
    final last = _last;
    if (!force &&
        last != null &&
        at != null &&
        _clock().difference(at) < validFor) {
      return Future.value(last);
    }
    // One check at a time: callers that arrive meanwhile share it, unless they asked for a new one after it started.
    final running = _running;
    if (running != null && !force) return running;
    final run = _run();
    _running = run;
    return run;
  }

  Future<IntegrityResult> _run() async {
    runs++;
    IntegrityResult result;
    try {
      result = await checker.check();
    } on Object {
      // A checker that fails is not a healthy phone.
      result = const IntegrityResult({IntegrityReason.deviceModified});
    }
    _last = result;
    _at = _clock();
    _running = null;
    return result;
  }
}
