import 'dart:convert';

import 'package:http/http.dart' as http;

import '../keys/device_key_store.dart';
import '../security/pinning/pinned_http.dart';
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

  /// Enrolls [key]. [loginToken] comes from a fresh full login (rule 7).
  /// [approvedByKid] names an already active device that approved this one,
  /// which makes the new device active at once.
  Future<EnrollmentRecord> enroll({
    required DeviceKey key,
    required String loginToken,
    String? approvedByKid,
    String? name,
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
          if (approvedByKid != null) 'approved_by_kid': approvedByKid,
          if (name != null) 'name': name,
        }),
      );
    } on PinningException catch (e) {
      throw EnrollmentException(e.message);
    } on Object catch (e) {
      throw EnrollmentException('Could not reach Biatec OIDC: $e');
    }
    return parseResponse(res.statusCode, res.body);
  }

  static EnrollmentRecord parseResponse(int status, String body) {
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
      // The mock reports epoch seconds.
      activeAt: DateTime.fromMillisecondsSinceEpoch((activeAt * 1000).round(),
          isUtc: true),
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
