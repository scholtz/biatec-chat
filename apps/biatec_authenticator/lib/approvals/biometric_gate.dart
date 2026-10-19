import 'package:local_auth/local_auth.dart';

/// Asks the user to prove presence before the device key signs an approval.
abstract class BiometricGate {
  /// True only when the user passed the check. Never throws.
  Future<bool> confirm(String reason);
}

/// Biometric check through the platform (Face ID, Touch ID, Android
/// BiometricPrompt). Fails closed: no enrolled biometrics, an error or a
/// cancel all mean no.
class LocalAuthBiometricGate implements BiometricGate {
  LocalAuthBiometricGate([LocalAuthentication? auth])
      : _auth = auth ?? LocalAuthentication();

  final LocalAuthentication _auth;

  @override
  Future<bool> confirm(String reason) async {
    try {
      return await _auth.authenticate(
        localizedReason: reason,
        biometricOnly: true,
      );
    } on Object {
      return false;
    }
  }
}
