import 'dart:convert';

import 'package:http/http.dart' as http;

/// `PUT /devices/push` at Biatec OIDC (the mock in development): tells it
/// where to send this device's notifications.
class PushRegistrationClient {
  PushRegistrationClient({required this.baseUrl, http.Client? client})
      : _client = client ?? http.Client();

  final String baseUrl;
  final http.Client _client;

  /// Returns normally on success and throws otherwise.
  Future<void> register({
    required String deviceToken,
    required String provider,
    required String pushToken,
  }) async {
    final res = await _client
        .put(
          Uri.parse('$baseUrl/devices/push'),
          headers: {
            'Authorization': 'Device $deviceToken',
            'Content-Type': 'application/json',
          },
          body: jsonEncode({'provider': provider, 'token': pushToken}),
        )
        .timeout(const Duration(seconds: 15));
    if (res.statusCode != 200) {
      throw Exception('Push registration refused (${res.statusCode}).');
    }
  }
}
