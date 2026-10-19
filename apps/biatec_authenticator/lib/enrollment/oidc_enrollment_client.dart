import 'dart:convert';

import 'package:http/http.dart' as http;

import '../keys/device_key_store.dart';
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
      : _client = client ?? http.Client();

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
    } on Exception catch (e) {
      throw EnrollmentException('Could not reach Biatec OIDC: $e');
    }
    return parseResponse(res.statusCode, res.body);
  }

  static EnrollmentRecord parseResponse(int status, String body) {
    Map<String, Object?> json;
    try {
      json = (jsonDecode(body) as Map).cast<String, Object?>();
    } on Object {
      throw EnrollmentException('Unexpected response ($status).');
    }
    if (status != 201) {
      final detail = json['error_description'] ?? json['error'] ?? 'error';
      throw EnrollmentException('Enrollment refused ($status): $detail');
    }
    final activeAt = json['active_at'];
    final token = json['device_token'];
    final kid = json['kid'];
    if (activeAt is! num || token is! String || kid is! String) {
      throw const EnrollmentException('Malformed enrollment response.');
    }
    return EnrollmentRecord(
      kid: kid,
      deviceToken: token,
      // The mock reports epoch seconds.
      activeAt: DateTime.fromMillisecondsSinceEpoch((activeAt * 1000).round(),
          isUtc: true),
      recoveryCode: json['recovery_code'] as String?,
    );
  }
}
