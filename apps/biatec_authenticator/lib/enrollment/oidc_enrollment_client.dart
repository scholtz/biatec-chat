import 'dart:convert';

import 'package:http/http.dart' as http;

import '../integrity/attestation.dart';
import '../keys/device_key_store.dart';
import '../security/pinning/pinned_http.dart';
import 'enrollment_proof.dart';
import 'enrollment_state.dart';

class EnrollmentException implements Exception {
  const EnrollmentException(this.message);
  final String message;
  @override
  String toString() => message;
}

/// `POST /devices` against Biatec OIDC (the mock in development).
class OidcEnrollmentClient {
  OidcEnrollmentClient({required this.baseUrl, http.Client? client})
      : _client = client ?? PinnedHttp.clientFor(baseUrl);

  final String baseUrl;
  final http.Client _client;

  /// Enrolls the two keys of [keys] with the enrollment [proofs]. [loginToken] comes from a fresh full login (rule 7).
  /// [approvedByKid] names an already active device that approved this one,
  /// which makes the new device active at once.
  Future<EnrollmentRecord> enroll({
    required DeviceKey key,
    DeviceKey? presenceKey,
    EnrollmentProofs? proofs,
    required String loginToken,
    String? approvedByKid,
    String? name,
    String? challenge,
    AttestationToken? platformAttestation,
  }) async {
    final http.Response res;
    try {
      res = await _client.post(
        Uri.parse('$baseUrl/devices'),
        headers: {
          'Authorization': 'Bearer $loginToken',
          'Content-Type': 'application/json',
        },
        body: jsonEncode({
          'kid': key.kid,
          'public_key': key.toJwk(),
          // Two-key mode (protocol 1.1): the second key and the proof of possession of both. The approval proof is signed by the approval key, the presence proof by the presence key, over the same payload and the server's nonce.
          if (presenceKey != null && proofs != null) ...{
            'presence_kid': presenceKey.kid,
            'presence_public_key': presenceKey.toJwk(),
            'approval_proof': proofs.approvalProof,
            'presence_proof': proofs.presenceProof,
          },
          if (approvedByKid != null) 'approved_by_kid': approvedByKid,
          if (name != null) 'name': name,
          // The platform's proof that this is the real app on a real device, bound to the nonce Biatec OIDC gave. Sent only when there is one: a missing proof is not replaced by a made-up one.
          if (platformAttestation != null && challenge != null) ...{
            'attestation_challenge': challenge,
            'platform_attestation': _attestationJson(platformAttestation),
          },
        }),
      );
    } on PinningException catch (e) {
      throw EnrollmentException(e.message);
    } on Object catch (e) {
      throw EnrollmentException('Could not reach Biatec OIDC: $e');
    }
    return parseResponse(res.statusCode, res.body,
        twoKeys: presenceKey != null);
  }

  static Map<String, Object?> _attestationJson(AttestationToken t) => {
        'kind': t.kind,
        'token': t.token,
        if (t.keyId != null) 'key_id': t.keyId,
      };

  /// A single-use nonce for a platform attestation, from a fresh login (enrollment) or with the device token (refresh). Null when Biatec OIDC does not offer one or cannot be reached: the enrollment then goes without an attestation.
  Future<String?> requestChallenge(
      {String? loginToken, String? deviceToken}) async {
    if (loginToken == null && deviceToken == null) return null;
    final auth =
        loginToken != null ? 'Bearer $loginToken' : 'Device $deviceToken';
    try {
      final res = await _client.post(Uri.parse('$baseUrl/devices/challenge'),
          headers: {'Authorization': auth});
      if (res.statusCode != 200) return null;
      final nonce = _decodeObject(res.body, res.statusCode)['nonce'];
      return nonce is String && nonce.isNotEmpty && nonce.length <= 512
          ? nonce
          : null;
    } on Object {
      return null;
    }
  }

  /// Sends a new platform attestation for an enrolled device. Returns when Biatec OIDC wants the next one, or null when it was not accepted.
  Future<DateTime?> refreshAttestation({
    required String deviceToken,
    required String challenge,
    required AttestationToken platformAttestation,
  }) async {
    try {
      final res = await _client.put(
        Uri.parse('$baseUrl/devices/attestation'),
        headers: {
          'Authorization': 'Device $deviceToken',
          'Content-Type': 'application/json',
        },
        body: jsonEncode({
          'attestation_challenge': challenge,
          'platform_attestation': _attestationJson(platformAttestation),
        }),
      );
      if (res.statusCode != 200) return null;
      return _refreshAfter(_decodeObject(res.body, res.statusCode));
    } on Object {
      return null;
    }
  }

  static DateTime? _refreshAfter(Map<String, Object?> json) {
    final v = json['attestation_refresh_after'];
    return v is num
        ? DateTime.fromMillisecondsSinceEpoch((v * 1000).round(), isUtc: true)
        : null;
  }

  static EnrollmentRecord parseResponse(int status, String body,
      {bool twoKeys = true}) {
    final json = _decodeObject(body, status);
    if (status != 201) {
      final detail = json['error_description'] ?? json['error'] ?? 'error';
      throw EnrollmentException('Enrollment refused ($status): $detail');
    }
    // The recovery code is deliberately not read here: rule 7 issues it only
    // once the device is active, through [requestRecoveryCode].
    final activeAt = json['active_at'];
    final token = json['device_token'];
    final kid = json['kid'];
    final attestation = json['attestation'];
    final presenceKid = json['presence_kid'];
    final presenceAttestation = json['presence_attestation'];
    if (twoKeys && (presenceKid is! String || presenceAttestation is! String)) {
      throw const EnrollmentException(
          'Biatec OIDC did not enroll both device keys.');
    }
    if (activeAt is! num ||
        token is! String ||
        kid is! String ||
        (attestation != null && attestation is! String)) {
      throw const EnrollmentException('Malformed enrollment response.');
    }
    return EnrollmentRecord(
      kid: kid,
      deviceToken: token,
      attestation: attestation as String?,
      presenceKid: presenceKid is String ? presenceKid : null,
      presenceAttestation:
          presenceAttestation is String ? presenceAttestation : null,
      // The mock reports epoch seconds.
      activeAt: DateTime.fromMillisecondsSinceEpoch((activeAt * 1000).round(),
          isUtc: true),
      attestationRefreshAfter: _refreshAfter(json),
    );
  }

  static Map<String, Object?> _decodeObject(String body, int status) {
    try {
      final decoded = jsonDecode(body);
      if (decoded is Map) return decoded.cast<String, Object?>();
    } on Object {
      // Falls through to the error below.
    }
    throw EnrollmentException('Unexpected response ($status).');
  }

  Future<http.Response> _deviceCall(String path, String deviceToken) async {
    try {
      return await _client.post(
        Uri.parse('$baseUrl$path'),
        headers: {'Authorization': 'Device $deviceToken'},
      );
    } on PinningException catch (e) {
      throw EnrollmentException(e.message);
    } on Object catch (e) {
      throw EnrollmentException('Could not reach Biatec OIDC: $e');
    }
  }

  /// Asks for the one-time recovery code. The server refuses while the device
  /// is still inactive (rule 7), so this is only called once it is active.
  Future<String> requestRecoveryCode(String deviceToken) async {
    final res = await _deviceCall('/devices/recovery-code', deviceToken);
    final json = _decodeObject(res.body, res.statusCode);
    final code = json['recovery_code'];
    if (res.statusCode != 200 || code is! String || code.isEmpty) {
      final detail = json['error_description'] ?? json['error'] ?? 'error';
      throw EnrollmentException(
          'Recovery code unavailable (${res.statusCode}): $detail');
    }
    return code;
  }

  /// Tells the server the user saved the code, so it is never issued again.
  Future<void> acknowledgeRecoveryCode(String deviceToken) async {
    final res = await _deviceCall('/devices/recovery-code/ack', deviceToken);
    if (res.statusCode != 200) {
      throw EnrollmentException(
          'Could not confirm the recovery code (${res.statusCode}).');
    }
  }
}
