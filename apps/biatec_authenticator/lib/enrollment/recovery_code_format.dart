/// The recovery code Biatec OIDC issues: three groups of four upper case hexadecimal characters (`1A2B-3C4D-5E6F`; `POST /devices/recovery-code`, `RecoveryCodeView`).
final _recoveryCode = RegExp(r'^[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}$');

/// [typed] in the form Biatec OIDC takes, or null when it cannot be a recovery code: spaces and a missing or different dash are forgiven, letters are upper-cased.
/// Checked before the browser opens: a typo then costs nothing, where a wrong code sent to Biatec OIDC counts toward the lockout and, because the login is
/// `max_age=0`, means signing in again.
String? normalizeRecoveryCode(String typed) {
  final compact = typed.toUpperCase().replaceAll(RegExp(r'[\s\-]'), '');
  if (compact.length != 12) return null;
  final code = '${compact.substring(0, 4)}-${compact.substring(4, 8)}-${compact.substring(8)}';
  return _recoveryCode.hasMatch(code) ? code : null;
}
