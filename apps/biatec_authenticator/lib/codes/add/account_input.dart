import 'dart:typed_data';

import '../../totp/core/base32.dart';
import '../../totp/core/otp_account.dart';
import '../../totp/core/otp_uri.dart';

/// A reason an entry was refused, in words for the user. It never contains the key.
class InputProblem implements Exception {
  const InputProblem(this.message);
  final String message;

  @override
  String toString() => 'InputProblem: $message';
}

/// The three ways to add an account all end in the same [OtpAccount]: from a scanned QR code or a pasted link ([fromLink]), or from a typed setup key ([fromKey]). Each refuses with the engine's own message.
abstract final class AccountInput {
  /// A QR code or a pasted `otpauth://` link.
  static OtpAccount fromLink(String link) {
    try {
      return OtpUri.parse(link.trim());
    } on OtpUriException catch (e) {
      throw InputProblem(e.message);
    }
  }

  /// A setup key typed in as the service shows it (Base32; spaces, dashes and case are tolerated).
  static OtpAccount fromKey({
    required String issuer,
    required String label,
    required String key,
    OtpAlgorithm algorithm = OtpAlgorithm.sha1,
    int digits = 6,
    int period = OtpLimits.defaultPeriod,
  }) {
    final Uint8List secret;
    try {
      secret = Base32.decode(key);
    } on Base32Exception catch (e) {
      throw InputProblem(e.message);
    }
    try {
      return OtpAccount(
        issuer: issuer,
        label: label,
        secret: secret,
        algorithm: algorithm,
        digits: digits,
        period: period,
      );
    } on OtpAccountException catch (e) {
      throw InputProblem(e.message as String);
    } finally {
      secret.fillRange(0, secret.length, 0);
    }
  }
}
