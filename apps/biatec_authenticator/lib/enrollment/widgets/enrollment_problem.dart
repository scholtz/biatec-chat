import 'dart:ui';

import '../enrollment_controller.dart';
import '../enrollment_strings.dart';
import '../onboarding_texts.dart';

/// Why an enrollment attempt failed, as far as the user can act on it.
enum EnrollmentProblemKind {
  /// Biatec OIDC could not be reached: nothing was enrolled, trying again may work.
  network,

  /// The connection did not pass the certificate pins: trying again will not help, an updated app may.
  unverified,

  /// The login token was missing or refused: a fresh login is needed.
  refusedLogin,

  /// Biatec OIDC already has this device.
  alreadyEnrolled,

  /// The device cannot keep the key in secure hardware.
  keyNotHardware,

  /// The recovery code typed at enrollment is wrong.
  wrongRecoveryCode,

  /// Too many wrong recovery codes: activation by recovery code is locked for a while.
  recoveryLocked,

  /// The sign-in in the browser was cancelled or did not give a token.
  signInFailed,

  /// This kind of device has no protected key store (a desktop build): nothing can be enrolled here, and trying again does not help.
  noKeyStore,

  /// This computer has no TPM that holds keys.
  noTpm,

  /// Anything else; the message says what the server or device said.
  other,
}

/// What to tell the user about a failed enrollment, and what they can do next.
class EnrollmentProblem {
  const EnrollmentProblem(
      this.kind, this.title, this.message, this.actionLabel);

  final EnrollmentProblemKind kind;
  final String title;

  /// Says what to do. Never contains the login token.
  final String message;

  /// The label of the one button that moves on.
  final String actionLabel;

  /// True when the action asks the device what it already has, instead of enrolling again.
  bool get checksAgain => kind == EnrollmentProblemKind.alreadyEnrolled;
}

/// The client's own prefix for a request that did not reach Biatec OIDC. Matched at the start, so a server's detail text that happens to say "could not reach" is not taken for it.
const _unreachablePrefix = 'Could not reach Biatec OIDC';

/// Whether [error] is the client's "could not reach Biatec OIDC" message.
bool isUnreachableError(String error) => error.startsWith(_unreachablePrefix);

/// Reads the message the controller reports (the client's messages are fixed English texts) into something the screen can answer specifically, in the
/// language of [locale] (the device's language when null). The error is recognised by its English text; what is shown is in that language.
EnrollmentProblem classifyEnrollmentError(String error, {Locale? locale}) {
  final lower = error.toLowerCase();
  final strings = EnrollStrings.of(locale);
  final t = OnboardingTexts.of(locale);
  if (error == EnrollmentController.recoveryWrongError) {
    return EnrollmentProblem(EnrollmentProblemKind.wrongRecoveryCode,
        strings.recoveryWrongTitle, strings.recoveryWrong, strings.signInButton);
  }
  if (error == EnrollmentController.recoveryFormatError) {
    return EnrollmentProblem(EnrollmentProblemKind.wrongRecoveryCode,
        strings.recoveryWrongTitle, strings.recoveryFormat, strings.signInButton);
  }
  if (error == EnrollmentController.recoveryLockedError) {
    return EnrollmentProblem(EnrollmentProblemKind.recoveryLocked,
        strings.recoveryLockedTitle, strings.recoveryLocked, strings.signInButton);
  }
  if (error == EnrollmentController.loginCancelledError) {
    return EnrollmentProblem(EnrollmentProblemKind.signInFailed,
        strings.signInButton, strings.loginCancelled, strings.signInButton);
  }
  if (error.startsWith(EnrollmentController.loginFailedPrefix)) {
    // The reason is a LoginFailed message of the login client (English), shown in the language of the screen.
    final why =
        error.substring(EnrollmentController.loginFailedPrefix.length).trim();
    return EnrollmentProblem(EnrollmentProblemKind.signInFailed,
        strings.signInButton, strings.loginFailed(t.message(why)), strings.signInButton);
  }
  if (error == EnrollmentController.noKeyStoreError) {
    return EnrollmentProblem(EnrollmentProblemKind.noKeyStore,
        strings.noKeyStoreTitle, strings.noKeyStore, strings.signInButton);
  }
  if (error == EnrollmentController.noTpmError) {
    return EnrollmentProblem(EnrollmentProblemKind.noTpm, strings.noTpmTitle,
        strings.noTpm, strings.signInButton);
  }
  if (error == EnrollmentController.staleLoginError) {
    return EnrollmentProblem(EnrollmentProblemKind.signInFailed,
        strings.signInAgainButton, strings.staleLoginAgain, strings.signInAgainButton);
  }
  // The pinning refusal (PinningException.message): "The connection to <host> could not be verified, so nothing was sent."
  if (error.startsWith('The connection to ') &&
      lower.contains('could not be verified')) {
    return EnrollmentProblem(EnrollmentProblemKind.unverified,
        t.text('unverifiedTitle'), t.text('unverified'), t.text('tryAgain'));
  }
  if (isUnreachableError(error)) {
    return EnrollmentProblem(EnrollmentProblemKind.network,
        t.text('networkTitle'), t.text('network'), t.text('tryAgain'));
  }
  if (error.startsWith('A fresh login is required')) {
    return EnrollmentProblem(EnrollmentProblemKind.refusedLogin,
        t.text('tokenNeededTitle'), t.text('tokenNeeded'), t.text('connectCreate'));
  }
  final refused = RegExp(r'refused \((\d{3})\)').firstMatch(lower);
  final status = refused == null ? null : int.parse(refused.group(1)!);
  // The status code decides first: a server's own wording ("token already used") must not turn a refused login into "already enrolled".
  // The server's status for a repeated device key is not fixed (409, or a 400 that says so), and the app mints a random key per attempt.
  if (status == 409 || (status == 400 && lower.contains('already enrolled'))) {
    return EnrollmentProblem(EnrollmentProblemKind.alreadyEnrolled,
        t.text('alreadyTitle'), t.text('already'), t.text('checkAgain'));
  }
  if (status == 400 || status == 401 || status == 403) {
    return EnrollmentProblem(EnrollmentProblemKind.refusedLogin,
        t.text('tokenRefusedTitle'), t.text('tokenRefused'), t.text('connectCreate'));
  }
  // The controller's refusals of a key that is not in secure hardware.
  if (error.startsWith('This device keeps the key in') ||
      error.startsWith('This device cannot say where it keeps the key') ||
      error.startsWith('This device cannot protect approvals')) {
    return EnrollmentProblem(
        EnrollmentProblemKind.keyNotHardware,
        t.text('keyNotHardwareTitle'),
        t.text('keyNotHardware', {'why': t.message(error)}),
        t.text('tryAgain'));
  }
  return EnrollmentProblem(EnrollmentProblemKind.other,
      t.text('enrollFailedTitle'), t.message(error), t.text('tryAgain'));
}
