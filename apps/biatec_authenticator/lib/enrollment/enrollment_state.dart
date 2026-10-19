/// Persisted result of a successful enrollment.
class EnrollmentRecord {
  const EnrollmentRecord({
    required this.kid,
    required this.deviceToken,
    required this.activeAt,
    this.recoveryCodeShown = false,
  });

  final String kid;

  /// Opaque token that authenticates this device to Biatec OIDC.
  final String deviceToken;

  /// When the device becomes active (a time in the past means active).
  final DateTime activeAt;

  /// Whether the user has confirmed saving the recovery code. The code itself
  /// is never part of the record: the server issues it only once the device is
  /// active (rule 7) and the app holds it in memory while it is on screen.
  final bool recoveryCodeShown;

  EnrollmentRecord withCodeShown() => EnrollmentRecord(
        kid: kid,
        deviceToken: deviceToken,
        activeAt: activeAt,
        recoveryCodeShown: true,
      );

  Map<String, Object?> toJson() => {
        'kid': kid,
        'device_token': deviceToken,
        'active_at': activeAt.toUtc().millisecondsSinceEpoch,
        'recovery_code_shown': recoveryCodeShown,
      };

  factory EnrollmentRecord.fromJson(Map<String, Object?> json) =>
      EnrollmentRecord(
        kid: json['kid'] as String,
        deviceToken: json['device_token'] as String,
        activeAt: DateTime.fromMillisecondsSinceEpoch(json['active_at'] as int,
            isUtc: true),
        recoveryCodeShown: json['recovery_code_shown'] as bool? ?? false,
      );
}

/// Where the enrollment flow stands.
enum EnrollmentPhase {
  /// No device key or enrollment yet.
  notEnrolled,

  /// Generating the hardware key.
  generatingKey,

  /// Calling Biatec OIDC.
  enrolling,

  /// Enrolled, but inactive during the 24 h delay (rule 7).
  inactive,

  /// Active; the recovery code has not been shown yet.
  activeRecoveryPending,

  /// Active and nothing left to show.
  active,

  /// The last attempt failed; [EnrollmentState.error] says why.
  failed,
}

class EnrollmentState {
  const EnrollmentState(this.phase, {this.record, this.error});

  final EnrollmentPhase phase;
  final EnrollmentRecord? record;
  final String? error;

  static const notEnrolled = EnrollmentState(EnrollmentPhase.notEnrolled);

  /// The state a stored [record] means at time [now].
  static EnrollmentState fromRecord(EnrollmentRecord record, DateTime now,
      {String? error}) {
    if (record.activeAt.isAfter(now)) {
      return EnrollmentState(EnrollmentPhase.inactive,
          record: record, error: error);
    }
    return EnrollmentState(
      record.recoveryCodeShown
          ? EnrollmentPhase.active
          : EnrollmentPhase.activeRecoveryPending,
      record: record,
      error: error,
    );
  }

  /// Time left until activation, or zero.
  Duration remaining(DateTime now) {
    final at = record?.activeAt;
    if (at == null || !at.isAfter(now)) return Duration.zero;
    return at.difference(now);
  }
}
