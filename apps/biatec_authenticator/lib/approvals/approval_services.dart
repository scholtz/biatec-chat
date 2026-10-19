import '../config.dart';
import '../enrollment/enrollment_store.dart';
import '../keys/device_key_store.dart';
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
    DateTime Function()? clock,
  }) : clock = clock ?? DateTime.now;

  factory ApprovalServices.fromConfig(AppConfig config) => ApprovalServices(
        client: ApprovalClient(baseUrl: config.oidcBaseUrl),
        keys: const PlatformDeviceKeyStore(),
        enrollment: SecureEnrollmentStore(),
        gate: LocalAuthBiometricGate(),
        history: SecureHistoryStore(),
      );

  final ApprovalClient client;
  final DeviceKeyStore keys;
  final EnrollmentStore enrollment;
  final BiometricGate gate;
  final HistoryStore history;
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
