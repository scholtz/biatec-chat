import '../config.dart';
import '../enrollment/enrollment_store.dart';
import '../enrollment/observable_enrollment_store.dart';
import '../keys/device_key_store.dart';
import '../security/device_proof.dart';
import '../security/touch_guard.dart';
import 'approval_client.dart';
import 'approval_controller.dart';
import 'biometric_gate.dart';
import 'history_store.dart';

/// Requests the user dismissed, each with the time it runs out (an id that has run out is forgotten). Kept in memory for this run of the app on purpose: persisting it would add a stored value to the privacy
/// inventory for little gain, and a request that comes back after a restart can simply be dismissed again.
class DismissedRequests {
  final Map<String, DateTime> _until = {};

  void add(String authReqId, DateTime expiresAt) => _until[authReqId] = expiresAt;

  bool contains(String authReqId) => _until.containsKey(authReqId);

  /// Forgets what has run out at [now].
  void prune(DateTime now) => _until.removeWhere((_, t) => !t.isAfter(now));

  int get length => _until.length;
}

/// Everything the approval screens need, built once from [AppConfig] and
/// replaced with fakes in tests.
class ApprovalServices {
  ApprovalServices({
    required this.client,
    required this.keys,
    required this.enrollment,
    required this.gate,
    required this.history,
    this.twoKeys = true,
    this.trustSettings = false,
    this.proof,
    this.touchGuard = const NoTouchGuard(),
    DateTime Function()? clock,
  }) : clock = clock ?? DateTime.now;

  /// [gate] is the app's lock: every fresh biometric (an approval, a settings change) goes through it, so none is asked while the app is locked.
  factory ApprovalServices.fromConfig(AppConfig config,
      {required BiometricGate gate}) {
    const keys = PlatformDeviceKeyStore();
    final enrollment = ObservableEnrollmentStore(SecureEnrollmentStore());
    // Every device call proves possession of the device key, not only the bearer token (BCHAT-030).
    final proof = KeystoreDeviceProofSigner(keys: keys, enrollment: enrollment);
    return ApprovalServices(
      client: ApprovalClient(
          baseUrl: config.oidcBaseUrl,
          proof: proof,
          relyingParties: config.relyingPartiesEnabled),
      keys: keys,
      enrollment: enrollment,
      gate: gate,
      history: SecureHistoryStore(),
      twoKeys: config.twoKeys,
      trustSettings: config.trustSettings,
      proof: proof,
      touchGuard: const PlatformTouchGuard(),
    );
  }

  final ApprovalClient client;
  final DeviceKeyStore keys;

  /// The app learns of a sign-out from it, and it keeps an enrollment whose write failed, so everything that reads the enrollment shares this one.
  final ObservableEnrollmentStore enrollment;
  final BiometricGate gate;
  final HistoryStore history;

  /// `AppConfig.twoKeys`; off against a Biatec OIDC without the two device keys.
  final bool twoKeys;

  /// `AppConfig.trustSettings`: the approval card offers "don't ask again" and says when a service is new.
  final bool trustSettings;

  /// Adds the proof of possession to device calls; null in tests that talk to a fake.
  final DeviceProofSigner? proof;

  /// Checked on the approve action (Android 10 and 11, see [TouchGuard]).
  final TouchGuard touchGuard;
  final DateTime Function() clock;

  /// Requests the user dismissed because this app cannot show them (an unknown type, a schema failure, text that breaks the rules). Local only, for this run of the app: nothing is sent to Biatec OIDC,
  /// and the request simply runs out there. They are hidden from the list and not counted in the badge.
  final DismissedRequests dismissed = DismissedRequests();

  ApprovalController controllerFor(String authReqId) => ApprovalController(
        authReqId: authReqId,
        client: client,
        keys: keys,
        enrollment: enrollment,
        history: history,
        gate: gate,
        twoKeys: twoKeys,
        trustSettings: trustSettings,
        clock: clock,
      );
}
