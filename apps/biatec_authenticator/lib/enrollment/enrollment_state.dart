/// Persisted result of a successful enrollment.
class EnrollmentRecord {
  const EnrollmentRecord({
    required this.kid,
    required this.deviceToken,
    required this.activeAt,
    this.attestation,
    this.presenceKid,
    this.presenceAttestation,
    this.recoveryCodeShown = false,
    this.attestationRefreshAfter,
  });

  final String kid;

  /// Opaque token that authenticates this device to Biatec OIDC.
  final String deviceToken;

  /// When the device becomes active (a time in the past means active).
  final DateTime activeAt;

  /// Biatec OIDC's attestation JWS binding this device key, returned at
  /// enrollment and presented again in every consent receipt (rule 6). Null
  /// for records saved before it was kept; such a device must re-enroll to
  /// approve.
  final String? attestation;

  /// The presence key (protocol 1.1: the second device key, usable without a prompt) and its attestation. [kid] and [attestation] are the approval key's. Null for a record saved before the two keys: such an install has only the one unprotected key and must enroll again (no silent migration).
  final String? presenceKid;
  final String? presenceAttestation;

  /// Both keys are enrolled. A record without them is not usable: the device enrolls again.
  bool get hasTwoKeys => presenceKid != null && presenceAttestation != null;

  /// Whether the user has confirmed saving the recovery code. The code itself
  /// is never part of the record: the server issues it only once the device is
  /// active (rule 7) and the app holds it in memory while it is on screen.
  final bool recoveryCodeShown;

  /// When Biatec OIDC wants the platform attestation (Play Integrity, App Attest) sent again, or null for a device enrolled without one.
  final DateTime? attestationRefreshAfter;

  EnrollmentRecord copyWith(
          {bool? recoveryCodeShown, DateTime? attestationRefreshAfter}) =>
      EnrollmentRecord(
        kid: kid,
        deviceToken: deviceToken,
        activeAt: activeAt,
        attestation: attestation,
        presenceKid: presenceKid,
        presenceAttestation: presenceAttestation,
        recoveryCodeShown: recoveryCodeShown ?? this.recoveryCodeShown,
        attestationRefreshAfter:
            attestationRefreshAfter ?? this.attestationRefreshAfter,
      );

  EnrollmentRecord withCodeShown() => copyWith(recoveryCodeShown: true);

  EnrollmentRecord withAttestationRefreshAfter(DateTime when) =>
      copyWith(attestationRefreshAfter: when);

  Map<String, Object?> toJson() => {
        'kid': kid,
        'device_token': deviceToken,
        'active_at': activeAt.toUtc().millisecondsSinceEpoch,
        if (attestation != null) 'attestation': attestation,
        if (presenceKid != null) 'presence_kid': presenceKid,
        if (presenceAttestation != null)
          'presence_attestation': presenceAttestation,
        'recovery_code_shown': recoveryCodeShown,
        if (attestationRefreshAfter != null)
          'attestation_refresh_after':
              attestationRefreshAfter!.toUtc().millisecondsSinceEpoch,
      };

  factory EnrollmentRecord.fromJson(Map<String, Object?> json) =>
      EnrollmentRecord(
        kid: json['kid'] as String,
        deviceToken: json['device_token'] as String,
        activeAt: DateTime.fromMillisecondsSinceEpoch(json['active_at'] as int,
            isUtc: true),
        attestation: json['attestation'] as String?,
        presenceKid: json['presence_kid'] as String?,
        presenceAttestation: json['presence_attestation'] as String?,
        recoveryCodeShown: json['recovery_code_shown'] as bool? ?? false,
        attestationRefreshAfter: json['attestation_refresh_after'] is int
            ? DateTime.fromMillisecondsSinceEpoch(
                json['attestation_refresh_after'] as int,
                isUtc: true)
            : null,
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
