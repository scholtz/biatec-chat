import '../config.dart';
import '../enrollment/enrollment_store.dart';
import '../enrollment/observable_enrollment_store.dart';
import '../keys/device_key_store.dart';
import '../security/device_proof.dart';
import 'approval_client.dart';
import 'approval_controller.dart';
import 'biometric_gate.dart';
import 'history_store.dart';

/// Everything the approval screens need, built once from [AppConfig] and
/// replaced with fakes in tests.
class ApprovalServices {
  ApprovalServices({
    required this.client,
    required this.keys,
    required this.enrollment,
    required this.gate,
    required this.history,
    this.proof,
    DateTime Function()? clock,
  }) : clock = clock ?? DateTime.now;

  factory ApprovalServices.fromConfig(AppConfig config) {
    const keys = PlatformDeviceKeyStore();
    final enrollment = ObservableEnrollmentStore(SecureEnrollmentStore());
    // Every device call proves possession of the device key, not only the bearer token (BCHAT-030).
    final proof = KeystoreDeviceProofSigner(keys: keys, enrollment: enrollment);
    return ApprovalServices(
      client: ApprovalClient(baseUrl: config.oidcBaseUrl, proof: proof),
      keys: keys,
      enrollment: enrollment,
      gate: LocalAuthBiometricGate(),
      history: SecureHistoryStore(),
      proof: proof,
    );
  }

  final ApprovalClient client;
  final DeviceKeyStore keys;
  /// The app learns of a sign-out from it, and it keeps an enrollment whose write failed, so everything that reads the enrollment shares this one.
  final ObservableEnrollmentStore enrollment;
  final BiometricGate gate;
  final HistoryStore history;

  /// Adds the proof of possession to device calls; null in tests that talk to a fake.
  final DeviceProofSigner? proof;
  final DateTime Function() clock;

  ApprovalController controllerFor(String authReqId) => ApprovalController(
        authReqId: authReqId,
        client: client,
        keys: keys,
        enrollment: enrollment,
        gate: gate,
        history: history,
        clock: clock,
      );
}
