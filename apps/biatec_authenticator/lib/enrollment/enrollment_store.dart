import 'dart:convert';

import 'package:flutter_secure_storage/flutter_secure_storage.dart';

import 'enrollment_state.dart';

abstract class EnrollmentStore {
  Future<EnrollmentRecord?> read();
  Future<void> write(EnrollmentRecord record);
  Future<void> clear();
}

class SecureEnrollmentStore implements EnrollmentStore {
  SecureEnrollmentStore([FlutterSecureStorage? storage])
      : _storage = storage ?? const FlutterSecureStorage();

  static const _key = 'biatec.enrollment';
  final FlutterSecureStorage _storage;

  @override
  Future<EnrollmentRecord?> read() async {
    final raw = await _storage.read(key: _key);
    if (raw == null) return null;
    return EnrollmentRecord.fromJson(
        (jsonDecode(raw) as Map).cast<String, Object?>());
  }

  @override
  Future<void> write(EnrollmentRecord record) =>
      _storage.write(key: _key, value: jsonEncode(record.toJson()));

  @override
  Future<void> clear() => _storage.delete(key: _key);
}

class MemoryEnrollmentStore implements EnrollmentStore {
  EnrollmentRecord? _record;

  @override
  Future<EnrollmentRecord?> read() async => _record;

  @override
  Future<void> write(EnrollmentRecord record) async => _record = record;

  @override
  Future<void> clear() async => _record = null;
}
