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

/// Reads the message the controller reports (the client's messages are fixed texts) into something the screen can answer specifically.
EnrollmentProblem classifyEnrollmentError(String error) {
  final lower = error.toLowerCase();
  if (lower.contains('could not be verified')) {
    return EnrollmentProblem(
        EnrollmentProblemKind.unverified,
        'The connection could not be verified',
        'Nothing was sent. If this keeps happening, update the app.',
        'Try again');
  }
  if (lower.contains('could not reach')) {
    return EnrollmentProblem(
        EnrollmentProblemKind.network,
        'Cannot reach Biatec OIDC',
        'Check your connection and try again. Nothing was enrolled.',
        'Try again');
  }
  if (lower.contains('fresh login is required')) {
    return EnrollmentProblem(
        EnrollmentProblemKind.refusedLogin,
        'Login token needed',
        'Sign in to Biatec OIDC to get a fresh login token, paste it below and connect.',
        'Connect and create the key');
  }
  final refused = RegExp(r'refused \((\d{3})\)').firstMatch(lower);
  final status = refused == null ? null : int.parse(refused.group(1)!);
  // The status code decides first: a server's own wording ("token already used") must not turn a refused login into "already enrolled".
  // The server's status for a repeated device key is not fixed (409, or a 400 that says so), and the app mints a random key per attempt.
  if (status == 409 || (status == 400 && lower.contains('already enrolled'))) {
    return EnrollmentProblem(
        EnrollmentProblemKind.alreadyEnrolled,
        'This device is already enrolled',
        'Biatec OIDC already has this device. If this app has the enrollment, '
            'check again to open it. If it does not, remove the device from '
            'your account on another trusted device, then connect again.',
        'Check again');
  }
  if (status == 400 || status == 401 || status == 403) {
    return EnrollmentProblem(
        EnrollmentProblemKind.refusedLogin,
        'Login token not accepted',
        'The token was refused, perhaps because it is old or was already used. '
            'Sign in to Biatec OIDC again to get a fresh one, replace the token '
            'below and connect.',
        'Connect and create the key');
  }
  if (lower.contains('not in secure hardware') ||
      lower.contains('cannot say where it keeps the key') ||
      lower.contains('enrollment is refused')) {
    return EnrollmentProblem(
        EnrollmentProblemKind.keyNotHardware,
        'This device cannot protect the key',
        'The key must be kept in secure hardware. $error',
        'Try again');
  }
  return EnrollmentProblem(
      EnrollmentProblemKind.other, 'Enrollment failed', error, 'Try again');
}
